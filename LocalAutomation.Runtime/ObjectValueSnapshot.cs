using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Stores a detached per-property snapshot of one runtime object so callers can materialize a fresh instance without
/// carrying over event subscriptions or other object identity.
/// </summary>
public sealed class ObjectValueSnapshot
{
    /// <summary>
    /// Creates one detached snapshot for the provided owner type and value map.
    /// </summary>
    public ObjectValueSnapshot(Type ownerType, IReadOnlyDictionary<string, JToken> values)
    {
        OwnerType = ownerType ?? throw new ArgumentNullException(nameof(ownerType));
        Values = values ?? throw new ArgumentNullException(nameof(values));
    }

    /// <summary>
    /// Gets the runtime type the snapshot was captured from.
    /// </summary>
    public Type OwnerType { get; }

    /// <summary>
    /// Gets the detached per-property values keyed by property name.
    /// </summary>
    public IReadOnlyDictionary<string, JToken> Values { get; }
}

/// <summary>
/// Captures detached object value snapshots and materializes fresh instances from them.
/// </summary>
public static class ObjectValueSnapshotService
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, PropertyInfo>> SnapshotPropertiesCache = new();
    private static readonly JsonSerializer Serializer = JsonSerializer.CreateDefault();

    /// <summary>
    /// Captures the copyable public property values from the provided object into a detached token bag.
    /// </summary>
    public static ObjectValueSnapshot Capture(object source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        Type ownerType = source.GetType();
        Dictionary<string, JToken> values = new(StringComparer.Ordinal);
        foreach ((string propertyName, PropertyInfo property) in GetSnapshotProperties(ownerType))
        {
            object? value = property.GetValue(source);
            values[propertyName] = value == null
                ? JValue.CreateNull()
                : JToken.FromObject(value, Serializer);
        }

        return new ObjectValueSnapshot(ownerType, values);
    }

    /// <summary>
    /// Applies one detached snapshot onto the provided destination object.
    /// </summary>
    public static void Apply(object destination, ObjectValueSnapshot snapshot)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (snapshot == null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        IReadOnlyDictionary<string, PropertyInfo> destinationProperties = GetSnapshotProperties(destination.GetType());
        foreach ((string propertyName, JToken token) in snapshot.Values)
        {
            if (!destinationProperties.TryGetValue(propertyName, out PropertyInfo? property))
            {
                continue;
            }

            object? value = token.Type == JTokenType.Null
                ? null
                : token.ToObject(property.PropertyType, Serializer);
            property.SetValue(destination, value);
        }
    }

    /// <summary>
    /// Creates a fresh detached clone of the provided object by round-tripping its copyable property values through a
    /// token snapshot.
    /// </summary>
    public static object CloneDetached(object source)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        ObjectValueSnapshot snapshot = Capture(source);
        object clone = Activator.CreateInstance(snapshot.OwnerType)
            ?? throw new InvalidOperationException($"Could not create detached snapshot clone for '{snapshot.OwnerType.FullName}'.");
        Apply(clone, snapshot);
        return clone;
    }

    /// <summary>
    /// Creates a fresh detached clone of the provided object by round-tripping its copyable property values through a
    /// token snapshot.
    /// </summary>
    public static T CloneDetached<T>(T source)
        where T : class
    {
        return (T)CloneDetached((object)source);
    }

    /// <summary>
    /// Returns the public instance properties that represent copyable object state for snapshotting.
    /// </summary>
    private static IReadOnlyDictionary<string, PropertyInfo> GetSnapshotProperties(Type ownerType)
    {
        return SnapshotPropertiesCache.GetOrAdd(ownerType, static type => type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.CanRead
                && property.CanWrite
                && property.GetIndexParameters().Length == 0
                && !string.Equals(property.Name, nameof(OperationOptions.OperationTarget), StringComparison.Ordinal)
                && property.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .ToDictionary(static property => property.Name, static property => property, StringComparer.Ordinal));
    }
}
