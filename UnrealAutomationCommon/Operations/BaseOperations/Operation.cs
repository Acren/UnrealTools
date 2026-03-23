using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// Returns the active parameter object cast back to the Unreal-specific parameter type.
    /// </summary>
    protected UnrealAutomationCommon.Operations.UnrealOperationParameters UnrealOperationParameters => (UnrealAutomationCommon.Operations.UnrealOperationParameters)base.OperationParameters;

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
        UnrealAutomationCommon.Operations.UnrealOperationParameters parameters = (UnrealAutomationCommon.Operations.UnrealOperationParameters)base.CreateParameters(existing);
        if (parameters.GetOptionsInstance(typeof(AdditionalArgumentsOptions)) == null)
        {
            parameters.EnsureOptionsInstance(typeof(AdditionalArgumentsOptions));
        }

        return parameters;
    }

    /// <summary>
    /// Keeps freeform additional arguments available through the Unreal option pipeline without requiring app-level
    /// adapter glue.
    /// </summary>
    protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
    {
        base.CollectRequiredOptionSetTypes(target, optionSetTypes);
        optionSetTypes.Add(typeof(AdditionalArgumentsOptions));
    }

    /// <summary>
    /// Returns the effective Unreal engine install for the current parameter state.
    /// </summary>
    public UnrealAutomationCommon.Unreal.Engine? GetTargetEngineInstall(UnrealAutomationCommon.Operations.UnrealOperationParameters operationParameters)
    {
        return operationParameters.Engine;
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

}
