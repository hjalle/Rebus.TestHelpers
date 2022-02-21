﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rebus.Exceptions;
using Rebus.Sagas;

#pragma warning disable 1998

namespace Rebus.TestHelpers.Internals;

/// <summary>
/// Implementation of <see cref="ISagaStorage"/> that "persists" saga data in memory. Saga data is serialized/deserialized using Newtonsoft JSON.NET
/// with some pretty robust settings, so inheritance and interfaces etc. can be used in the saga data.
/// </summary>
class InMemorySagaStorage : ISagaStorage
{
    readonly ConcurrentDictionary<Guid, ISagaData> _data = new ConcurrentDictionary<Guid, ISagaData>();
    readonly ConcurrentDictionary<Guid, ISagaData> _previousDatas = new ConcurrentDictionary<Guid, ISagaData>();
    readonly object _lock = new object();
    readonly ISagaSerializer _sagaSerializer;
    public InMemorySagaStorage(ISagaSerializer sagaSerializer)
    {
        _sagaSerializer = sagaSerializer ?? throw new ArgumentNullException(nameof(sagaSerializer));
    }

    internal IEnumerable<ISagaData> Instances
    {
        get
        {
            lock (_lock)
            {
                return _data.Values.ToList();
            }
        }
    }

    internal void AddInstance(ISagaData sagaData)
    {
        lock (_lock)
        {
            var instance = Clone(sagaData);
            if (instance.Id == Guid.Empty)
            {
                instance.Id = Guid.NewGuid();
            }
            SaveSagaData(instance);
        }
    }

    internal event Action<ISagaData> Created;
    internal event Action<ISagaData> Updated;
    internal event Action<ISagaData> Deleted;
    internal event Action<ISagaData> Correlated;
    internal event Action CouldNotCorrelate;

    readonly ConcurrentDictionary<Guid, ISagaData> _sagaDatasToCauseConflict = new ConcurrentDictionary<Guid, ISagaData>();


    public void PrepareConflict(ISagaData sagaData)
    {
        if (!_previousDatas.ContainsKey(sagaData.Id))
        {
            throw new ArgumentException($"Cannot prepare conflict for saga data of type {sagaData.GetType()} and ID {sagaData.Id}, because there's no previous version stored");
        }

        _sagaDatasToCauseConflict[sagaData.Id] = sagaData;
    }

    /// <summary>
    /// Looks up an existing saga data of the given type with a property of the specified name and the specified value
    /// </summary>
    public async Task<ISagaData> Find(Type sagaDataType, string propertyName, object propertyValue)
    {
        lock (_lock)
        {
            var valueFromMessage = (propertyValue ?? "").ToString();

            foreach (var data in _data.Values)
            {
                if (data.GetType() != sagaDataType) continue;

                var sagaValue = Reflect.Value(data, propertyName);
                var valueFromSaga = (sagaValue ?? "").ToString();

                if (valueFromMessage.Equals(valueFromSaga))
                {
                    var id = data.Id;

                    if (_sagaDatasToCauseConflict.ContainsKey(id))
                    {
                        if (!_previousDatas.TryGetValue(id, out var previousSagaData))
                        {
                            throw new ArgumentException($"Sorry, but weirdly the saga data ID {id} could not be found in the storage for previous saga data versions");
                        }
                        var cloneOfPreviousSagaData = Clone(previousSagaData);
                        _sagaDatasToCauseConflict.TryRemove(id, out _);
                        Correlated?.Invoke(cloneOfPreviousSagaData);
                        return cloneOfPreviousSagaData;
                    }

                    var clone = Clone(data);
                    Correlated?.Invoke(clone);
                    return clone;
                }
            }

            CouldNotCorrelate?.Invoke();
            return null;
        }
    }

    /// <summary>
    /// Saves the given saga data, throwing an exception if the instance already exists
    /// </summary>
    public async Task Insert(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
    {
        var id = GetId(sagaData);

        lock (_lock)
        {
            if (_data.ContainsKey(id))
            {
                throw new ConcurrencyException($"Saga data with ID {id} already exists!");
            }

            VerifyCorrelationPropertyUniqueness(sagaData, correlationProperties);

            if (sagaData.Revision != 0)
            {
                throw new InvalidOperationException($"Attempted to insert saga data with ID {id} and revision {sagaData.Revision}, but revision must be 0 on first insert!");
            }

            var clone = Clone(sagaData);
            SaveSagaData(clone);
            Created?.Invoke(clone);
        }
    }

    /// <summary>
    /// Updates the saga data
    /// </summary>
    public async Task Update(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
    {
        var id = GetId(sagaData);

        lock (_lock)
        {
            if (!_data.ContainsKey(id))
            {
                throw new ConcurrencyException($"Saga data with ID {id} no longer exists and cannot be updated");
            }

            VerifyCorrelationPropertyUniqueness(sagaData, correlationProperties);

            var existingCopy = _data[id];

            if (existingCopy.Revision != sagaData.Revision)
            {
                throw new ConcurrencyException($"Attempted to update saga data with ID {id} with revision {sagaData.Revision}, but the existing data was updated to revision {existingCopy.Revision}");
            }

            var clone = Clone(sagaData);
            clone.Revision++;

            SaveSagaData(clone);

            Updated?.Invoke(clone);
            sagaData.Revision++;
        }
    }

    /// <summary>
    /// Deletes the given saga data
    /// </summary>
    public async Task Delete(ISagaData sagaData)
    {
        var id = GetId(sagaData);

        lock (_lock)
        {
            if (!_data.ContainsKey(id))
            {
                throw new ConcurrencyException($"Saga data with ID {id} no longer exists and cannot be deleted");
            }

            if (_data.TryRemove(id, out var previousSagaData))
            {
                _previousDatas[id] = previousSagaData;
                Deleted?.Invoke(previousSagaData);
            }
            sagaData.Revision++;
        }
    }

    void VerifyCorrelationPropertyUniqueness(ISagaData sagaData, IEnumerable<ISagaCorrelationProperty> correlationProperties)
    {
        foreach (var property in correlationProperties)
        {
            var valueFromSagaData = Reflect.Value(sagaData, property.PropertyName);

            foreach (var existingSagaData in _data.Values)
            {
                if (existingSagaData.Id == sagaData.Id) continue;
                if (existingSagaData.GetType() != sagaData.GetType()) continue;

                var valueFromExistingInstance = Reflect.Value(existingSagaData, property.PropertyName);

                if (Equals(valueFromSagaData, valueFromExistingInstance))
                {
                    throw new ConcurrencyException($"Correlation property '{property.PropertyName}' has value '{valueFromExistingInstance}' in existing saga data with ID {existingSagaData.Id}");
                }
            }
        }
    }

    void SaveSagaData(ISagaData sagaData)
    {
        var id = sagaData.Id;

        if (_data.TryGetValue(id, out var previousSagaData))
        {
            _previousDatas[id] = previousSagaData;
        }
        else
        {
            // if we haven't stored the previous version of the saga data, we do so here
            _previousDatas[id] = sagaData;
        }

        _data[id] = sagaData;
    }

    ISagaData Clone(ISagaData sagaData)
    {
        var serializedObject = _sagaSerializer.SerializeToString(sagaData);
        return _sagaSerializer.DeserializeFromString(sagaData.GetType(), serializedObject);
    }

    static Guid GetId(ISagaData sagaData)
    {
        var id = sagaData.Id;

        if (id != Guid.Empty) return id;

        throw new InvalidOperationException("Saga data must be provided with an ID in order to do this!");
    }
}