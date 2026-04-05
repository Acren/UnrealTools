using System;
using System.Reflection;
using LocalAutomation.Runtime;

namespace LocalAutomation.Application;

/// <summary>
/// Describes one runtime-editable persisted property and the metadata needed to read and write it.
/// </summary>
public sealed class PersistedSettingDescriptor
{
    /// <summary>
    /// Creates a descriptor for one persisted property.
    /// </summary>
    public PersistedSettingDescriptor(PropertyInfo property, string key, PersistenceScope writeScope)
    {
        Property = property ?? throw new ArgumentNullException(nameof(property));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        WriteScope = writeScope;
        ValueType = property.PropertyType;
    }

    /// <summary>
    /// Gets the reflected property that owns the value.
    /// </summary>
    public PropertyInfo Property { get; }

    /// <summary>
    /// Gets the stable persistence key for this property.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the storage layer that should receive edits for this property.
    /// </summary>
    public PersistenceScope WriteScope { get; }

    /// <summary>
    /// Gets the underlying value type that should be serialized.
    /// </summary>
    public Type ValueType { get; }
}
