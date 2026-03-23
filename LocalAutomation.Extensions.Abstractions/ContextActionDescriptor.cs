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
    public ContextActionDescriptor(ContextActionId id, string displayName, Type targetType, Action<object> execute, Func<object, bool>? canExecute = null)
    {
        Id = id;
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        TargetType = targetType ?? throw new ArgumentNullException(nameof(targetType));
        Execute = execute ?? throw new ArgumentNullException(nameof(execute));
        CanExecute = canExecute;
    }

    /// <summary>
    /// Gets the stable identifier used to reference this action across the host application.
    /// </summary>
    public ContextActionId Id { get; }

    /// <summary>
    /// Gets the label that should be shown to users when the host renders this action.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the runtime target type this action can operate on.
    /// </summary>
    public Type TargetType { get; }

    /// <summary>
    /// Gets the action invoked when the host executes this context action for a compatible target.
    /// </summary>
    public Action<object> Execute { get; }

    /// <summary>
    /// Gets the optional predicate used to suppress actions for compatible targets that fail extra runtime checks.
    /// </summary>
    public Func<object, bool>? CanExecute { get; }

    /// <summary>
    /// Returns whether the provided runtime target is compatible with this action.
    /// </summary>
    public bool AppliesTo(object target)
    {
        if (target == null)
        {
            return false;
        }

        if (!TargetType.IsInstanceOfType(target))
        {
            return false;
        }

        return CanExecute?.Invoke(target) ?? true;
    }
}
