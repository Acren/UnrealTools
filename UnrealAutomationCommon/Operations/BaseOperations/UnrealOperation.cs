using System;
using System.Linq;
using LocalAutomation.Core;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

#nullable enable

namespace UnrealAutomationCommon.Operations.BaseOperations;

/// <summary>
/// Preserves the historical Unreal base operation type name while the canonical implementation lives in the shared
/// runtime project.
/// </summary>
public abstract class UnrealOperation : global::LocalAutomation.Runtime.Operation
{
    /// <summary>
    /// Creates a new Unreal parameter object so Unreal-specific engine resolution remains available during option
    /// discovery and child operation flows.
    /// </summary>
    protected override global::LocalAutomation.Runtime.OperationParameters CreateOperationParameters()
    {
        return new UnrealAutomationCommon.Operations.UnrealOperationParameters();
    }

    /// <summary>
    /// Creates Unreal-specific parameter state and preserves any compatible shared state from an existing parameter set.
    /// </summary>
    public override global::LocalAutomation.Runtime.OperationParameters CreateParameters(global::LocalAutomation.Runtime.OperationParameters? existing = null)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("CreateOperationParameters");
        PerformanceTelemetry.SetTag(activity, "operation.type", GetType().Name);
        PerformanceTelemetry.SetTag(activity, "existing_option_set.count", existing?.OptionsInstances.Count ?? 0);

        UnrealAutomationCommon.Operations.UnrealOperationParameters parameters = (UnrealAutomationCommon.Operations.UnrealOperationParameters)base.CreateParameters(existing);
        if (parameters.GetOptionsInstance(typeof(AdditionalArgumentsOptions)) == null)
        {
            parameters.EnsureOptionsInstance(typeof(AdditionalArgumentsOptions));
        }

        PerformanceTelemetry.SetTag(activity, "new_option_set.count", parameters.OptionsInstances.Count);

        return parameters;
    }

    /// <summary>
    /// Keeps freeform additional arguments available through the Unreal option pipeline without requiring app-level
    /// adapter glue.
    /// </summary>
    protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
    {
        base.CollectRequiredOptionSetTypes(target, optionSetTypes);
        optionSetTypes.Add(typeof(EngineVersionOptions));
        optionSetTypes.Add(typeof(AdditionalArgumentsOptions));
    }

    /// <summary>
    /// Returns the effective Unreal engine install for the current parameter state.
    /// </summary>
    public UnrealAutomationCommon.Unreal.Engine? GetTargetEngineInstall(UnrealAutomationCommon.Operations.UnrealOperationParameters operationParameters)
    {
        return operationParameters.Engine;
    }

    /// <summary>
    /// Returns the resolved Unreal engine install or throws when command generation is attempted before validation has
    /// guaranteed one exists.
    /// </summary>
    protected UnrealAutomationCommon.Unreal.Engine GetRequiredTargetEngineInstall(UnrealAutomationCommon.Operations.UnrealOperationParameters operationParameters)
    {
        return GetTargetEngineInstall(operationParameters)
            ?? throw new InvalidOperationException("Operation requires a resolved Unreal engine install before execution.");
    }

    /// <summary>
    /// Returns the execution parameters from one scheduled task callback cast back to the Unreal-specific parameter
    /// model.
    /// </summary>
    protected UnrealAutomationCommon.Operations.UnrealOperationParameters GetUnrealOperationParameters(global::LocalAutomation.Runtime.ExecutionTaskContext context)
    {
        return (UnrealAutomationCommon.Operations.UnrealOperationParameters)context.OperationParameters;
    }

}

/// <summary>
/// Preserves the historical generic Unreal base operation type while using the shared runtime implementation.
/// </summary>
public abstract class UnrealOperation<T> : UnrealOperation where T : global::LocalAutomation.Runtime.IOperationTarget
{
    /// <summary>
    /// Creates a typed Unreal operation instance for the provided runtime type.
    /// </summary>
    public new static UnrealOperation<T> CreateOperation(System.Type operationType)
    {
        return (UnrealOperation<T>)System.Activator.CreateInstance(operationType)!;
    }

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
    public new T? GetTarget(global::LocalAutomation.Runtime.OperationParameters operationParameters)
    {
        return (T?)operationParameters.Target;
    }

    /// <summary>
    /// Returns the current target cast to the required Unreal target type.
    /// </summary>
    public T? GetTarget(UnrealAutomationCommon.Operations.UnrealOperationParameters operationParameters)
    {
        return (T?)operationParameters.Target;
    }

    /// <summary>
    /// Returns the current target or throws when execution reaches a path that depends on prior validation.
    /// </summary>
    protected T GetRequiredTarget(global::LocalAutomation.Runtime.OperationParameters operationParameters)
    {
        return GetTarget(operationParameters)
            ?? throw new InvalidOperationException($"Operation {GetType().Name} requires a target of type {typeof(T).Name}.");
    }

    /// <summary>
    /// Returns the current target or throws when execution reaches a path that depends on prior validation.
    /// </summary>
    protected T GetRequiredTarget(UnrealAutomationCommon.Operations.UnrealOperationParameters operationParameters)
    {
        return GetTarget(operationParameters)
            ?? throw new InvalidOperationException($"Operation {GetType().Name} requires a target of type {typeof(T).Name}.");
    }

}
