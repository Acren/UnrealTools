using System;
using System.Linq;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

#nullable enable

namespace UnrealAutomationCommon.Operations.BaseOperations;

/// <summary>
/// Preserves the historical Unreal base operation type name while the canonical implementation lives in the shared
/// runtime project.
/// </summary>
public abstract class UnrealOperation : global::LocalAutomation.Runtime.Operation
{
    /// <summary>
     /// Unreal operations expose engine version selection, but they now read freeform additional arguments directly from
     /// the shared parameter bag instead of mirroring them through a second Unreal-specific option set.
     /// </summary>
    protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
    {
        return base.GetDeclaredOptionSetTypes(target)
            .Concat(new[]
            {
                typeof(EngineVersionOptions)
            });
    }

    /// <summary>
     /// Returns the effective Unreal engine install for the current parameter state.
     /// </summary>
    public UnrealAutomationCommon.Unreal.Engine? GetTargetEngineInstall(ValidatedOperationParameters operationParameters)
    {
        EngineVersionOptions versionOptions = operationParameters.GetOptions<EngineVersionOptions>();
        if (versionOptions.EnabledVersions.Count > 0)
        {
            EngineVersion version = versionOptions.EnabledVersions[0];
            return EngineFinder.GetEngineInstall(version);
        }

        if (operationParameters.Target is not IEngineInstanceProvider engineInstanceProvider)
        {
            return null;
        }

        return engineInstanceProvider.EngineInstance;
    }

    /// <summary>
    /// Returns the resolved Unreal engine install or throws when command generation is attempted before validation has
    /// guaranteed one exists.
    /// </summary>
    protected UnrealAutomationCommon.Unreal.Engine GetRequiredTargetEngineInstall(ValidatedOperationParameters operationParameters)
    {
        return GetTargetEngineInstall(operationParameters)
            ?? throw new InvalidOperationException("Operation requires a resolved Unreal engine install before execution.");
    }

    /// <summary>
    /// Returns whether the current engine-version selection fits the expected single-engine shape used by operations
    /// that can also fall back to the target-derived engine.
    /// </summary>
    protected bool HasValidSingleEngineSelection(ValidatedOperationParameters operationParameters)
    {
        return operationParameters.GetOptions<EngineVersionOptions>().EnabledVersions.Count <= 1;
    }

    /// <summary>
    /// Returns the validation message for single-engine operations when too many explicit engine versions are selected.
    /// </summary>
    protected string? GetSingleEngineSelectionValidationMessage(ValidatedOperationParameters operationParameters)
    {
        if (HasValidSingleEngineSelection(operationParameters))
        {
            return null;
        }

        return "Select at most one engine version, or clear the selection to use the target engine";
    }

}

/// <summary>
/// Preserves the historical generic Unreal base operation type while using the shared runtime implementation.
/// </summary>
public abstract class UnrealOperation<T> : UnrealOperation where T : global::LocalAutomation.Runtime.IOperationTarget
{
    /// <summary>
    /// Returns whether the provided target matches the required target type.
    /// </summary>
    public override bool SupportsTarget(global::LocalAutomation.Runtime.IOperationTarget target)
    {
        return target is T;
    }

    /// <summary>
    /// Returns the current target cast to the required Unreal target type.
    /// </summary>
    public new T? GetTarget(OperationParameters operationParameters)
    {
        return (T?)operationParameters.Target;
    }

    /// <summary>
    /// Returns the current target cast to the required Unreal target type from the validated parameter view.
    /// </summary>
    public new T GetTarget(ValidatedOperationParameters operationParameters)
    {
        return operationParameters.GetTarget<T>();
    }

    /// <summary>
    /// Returns the current target or throws when execution reaches a path that depends on prior validation.
    /// </summary>
    protected T GetRequiredTarget(OperationParameters operationParameters)
    {
        return GetTarget(operationParameters)
            ?? throw new InvalidOperationException($"Operation {GetType().Name} requires a target of type {typeof(T).Name}.");
    }

    /// <summary>
    /// Returns the current target or throws when execution reaches a path that depends on prior validation.
    /// </summary>
    protected T GetRequiredTarget(ValidatedOperationParameters operationParameters)
    {
        return GetTarget(operationParameters);
    }

}
