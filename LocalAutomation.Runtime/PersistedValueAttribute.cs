using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Overrides the default persistence behavior for one editable property.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class PersistedValueAttribute : Attribute
{
    /// <summary>
    /// Creates persistence metadata for one editable property.
    /// </summary>
    public PersistedValueAttribute(PersistenceScope writeScope = PersistenceScope.UserTargetOverride, string? key = null)
    {
        WriteScope = writeScope;
        Key = key;
    }

    /// <summary>
    /// Gets the storage layer that should receive edits for the attributed property.
    /// </summary>
    public PersistenceScope WriteScope { get; }

    /// <summary>
    /// Gets the optional explicit stable key used instead of the generated convention-based key.
    /// </summary>
    public string? Key { get; }
}
