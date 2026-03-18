using System;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Describes a target-specific context action that an extension wants to expose in the host UI.
/// </summary>
public sealed class ContextActionDescriptor
{
    /// <summary>
    /// Creates a context action descriptor with a stable identifier, a user-facing label, and the target type it
    /// applies to.
    /// </summary>
    public ContextActionDescriptor(string id, string displayName, Type targetType)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
    }

    /// <summary>
    /// Gets the stable identifier used to reference this action across the host application.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the label that should be shown to users when the host renders this action.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the runtime target type this action can operate on.
    /// </summary>
    public Type TargetType { get; }
}
