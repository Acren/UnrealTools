using System;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Describes a target type contributed by an extension, including the runtime type used by current bridge code.
/// </summary>
public sealed class TargetDescriptor
{
    /// <summary>
    /// Creates a target descriptor with a stable identifier, display name, and runtime target type.
    /// </summary>
    public TargetDescriptor(string id, string displayName, Type targetType)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
    }

    /// <summary>
    /// Gets the stable identifier used to reference this target type.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the user-facing target type name shown by hosts.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the runtime type currently used to create and inspect target instances.
    /// </summary>
    public Type TargetType { get; }
}
