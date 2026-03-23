using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Assigns a stable generated-key prefix to a settings owner when persisted values do not provide explicit keys.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PersistedSettingsAttribute : Attribute
{
    /// <summary>
    /// Creates a persisted-settings attribute with the provided key prefix.
    /// </summary>
    public PersistedSettingsAttribute(string keyPrefix)
    {
        KeyPrefix = keyPrefix ?? throw new ArgumentNullException(nameof(keyPrefix));
    }

    /// <summary>
    /// Gets the stable generated-key prefix for persisted values owned by the attributed type.
    /// </summary>
    public string KeyPrefix { get; }
}
