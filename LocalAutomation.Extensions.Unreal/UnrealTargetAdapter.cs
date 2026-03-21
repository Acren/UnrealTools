using System;
using LocalAutomation.Extensions.Abstractions;
using UnrealAutomationCommon.Operations;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Adapts legacy Unreal runtime targets onto the generic target-inspection contract used by the application layer.
/// </summary>
public sealed class UnrealTargetAdapter : ITargetAdapter
{
    /// <summary>
    /// Gets the stable adapter identifier.
    /// </summary>
    public string Id => "unreal.target-adapter";

    /// <summary>
    /// Returns whether the provided runtime target belongs to the legacy Unreal target hierarchy.
    /// </summary>
    public bool CanAdapt(object target)
    {
        return target is IOperationTarget;
    }

    /// <summary>
    /// Returns whether the provided Unreal target is currently valid.
    /// </summary>
    public bool IsValid(object target)
    {
        return GetTarget(target).IsValid;
    }

    /// <summary>
    /// Returns the display name for the provided Unreal target.
    /// </summary>
    public string GetDisplayName(object target)
    {
        return GetTarget(target).DisplayName;
    }

    /// <summary>
    /// Returns the type label for the provided Unreal target.
    /// </summary>
    public string GetTypeName(object target)
    {
        return GetTarget(target).TypeName;
    }

    /// <summary>
    /// Returns the stable target path for the provided Unreal target.
    /// </summary>
    public string GetTargetPath(object target)
    {
        return GetTarget(target).TargetPath;
    }

    /// <summary>
    /// Casts the current bridge-era runtime target into the Unreal target hierarchy.
    /// </summary>
    private static IOperationTarget GetTarget(object target)
    {
        return target as IOperationTarget ?? throw new ArgumentException("Target is not a supported Unreal target.", nameof(target));
    }
}
