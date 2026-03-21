using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace UnrealAutomationCommon.Operations.BaseOperations;

/// <summary>
/// Preserves the historical Unreal base operation type name while the canonical implementation lives in the shared
/// runtime project.
/// </summary>
public abstract class Operation : global::LocalAutomation.Runtime.Operation
{
    /// <summary>
    /// Returns the active parameter object cast back to the Unreal-specific parameter type.
    /// </summary>
    protected new UnrealAutomationCommon.Operations.OperationParameters OperationParameters => (UnrealAutomationCommon.Operations.OperationParameters)base.OperationParameters;

    /// <summary>
    /// Creates a new Unreal parameter object so Unreal-specific engine resolution remains available during option
    /// discovery and child operation flows.
    /// </summary>
    protected override global::LocalAutomation.Runtime.OperationParameters CreateOperationParameters()
    {
        return new UnrealAutomationCommon.Operations.OperationParameters();
    }

    /// <summary>
    /// Creates Unreal-specific parameter state and preserves any compatible shared state from an existing parameter set.
    /// </summary>
    public override global::LocalAutomation.Runtime.OperationParameters CreateParameters(global::LocalAutomation.Runtime.OperationParameters? existing = null)
    {
        UnrealAutomationCommon.Operations.OperationParameters parameters = (UnrealAutomationCommon.Operations.OperationParameters)base.CreateParameters(existing);
        if (parameters.GetOptionsInstance(typeof(UnrealAutomationCommon.Operations.OperationOptionTypes.AdditionalArgumentsOptions)) == null)
        {
            parameters.SetOptions(new UnrealAutomationCommon.Operations.OperationOptionTypes.AdditionalArgumentsOptions());
        }

        return parameters;
    }

    /// <summary>
    /// Keeps freeform additional arguments available through the Unreal option pipeline without requiring app-level
    /// adapter glue.
    /// </summary>
    public sealed override HashSet<System.Type> GetRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
    {
        HashSet<System.Type> optionTypes = base.GetRequiredOptionSetTypes(target);
        optionTypes.Add(typeof(UnrealAutomationCommon.Operations.OperationOptionTypes.AdditionalArgumentsOptions));
        return optionTypes;
    }

    /// <summary>
    /// Bridges the shared runtime validation flow onto the Unreal-specific parameter type.
    /// </summary>
    public sealed override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.OperationParameters operationParameters)
    {
        return CheckRequirementsSatisfied((UnrealAutomationCommon.Operations.OperationParameters)operationParameters);
    }

    /// <summary>
    /// Lets Unreal operations validate against the Unreal-specific parameter model.
    /// </summary>
    public virtual string? CheckRequirementsSatisfied(UnrealAutomationCommon.Operations.OperationParameters operationParameters)
    {
        return base.CheckRequirementsSatisfied(operationParameters);
    }

    /// <summary>
    /// Bridges the shared runtime log-path query onto the Unreal-specific parameter type.
    /// </summary>
    public sealed override string? GetLogsPath(global::LocalAutomation.Runtime.OperationParameters operationParameters)
    {
        return GetLogsPath((UnrealAutomationCommon.Operations.OperationParameters)operationParameters);
    }

    /// <summary>
    /// Lets Unreal operations return log paths from the Unreal-specific parameter model.
    /// </summary>
    public virtual string? GetLogsPath(UnrealAutomationCommon.Operations.OperationParameters operationParameters)
    {
        return base.GetLogsPath(operationParameters);
    }

    /// <summary>
    /// Returns whether the current target can resolve an Unreal engine install.
    /// </summary>
    public bool TargetProvidesEngineInstall(UnrealAutomationCommon.Operations.OperationParameters operationParameters)
    {
        return GetTarget(operationParameters) is UnrealAutomationCommon.Unreal.IEngineInstanceProvider;
    }

    /// <summary>
    /// Returns the effective Unreal engine install for the current parameter state.
    /// </summary>
    public UnrealAutomationCommon.Unreal.Engine? GetTargetEngineInstall(UnrealAutomationCommon.Operations.OperationParameters operationParameters)
    {
        return operationParameters.Engine;
    }

    /// <summary>
    /// Bridges command generation from the shared runtime onto the Unreal-specific command model.
    /// </summary>
    protected sealed override IEnumerable<global::LocalAutomation.Runtime.Command> BuildCommands(global::LocalAutomation.Runtime.OperationParameters operationParameters)
    {
        return BuildCommands((UnrealAutomationCommon.Operations.OperationParameters)operationParameters);
    }

    /// <summary>
    /// Lets Unreal operations build command previews from the Unreal-specific parameter model.
    /// </summary>
    protected abstract IEnumerable<global::LocalAutomation.Runtime.Command> BuildCommands(UnrealAutomationCommon.Operations.OperationParameters operationParameters);

    /// <summary>
    /// Bridges runtime execution onto the Unreal-specific operation result model.
    /// </summary>
    protected sealed override async Task<global::LocalAutomation.Runtime.OperationResult> OnExecuted(CancellationToken token)
    {
        return await OnExecutedUnreal(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Lets Unreal operations implement execution using the Unreal-specific result model.
    /// </summary>
    protected abstract Task<OperationResult> OnExecutedUnreal(CancellationToken token);
}

/// <summary>
/// Preserves the historical generic Unreal base operation type while using the shared runtime implementation.
/// </summary>
public abstract class Operation<T> : Operation where T : global::LocalAutomation.Runtime.IOperationTarget
{
    /// <summary>
    /// Creates a typed Unreal operation instance for the provided runtime type.
    /// </summary>
    public new static Operation<T> CreateOperation(System.Type operationType)
    {
        return (Operation<T>)System.Activator.CreateInstance(operationType)!;
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
    public T? GetTarget(UnrealAutomationCommon.Operations.OperationParameters operationParameters)
    {
        return (T?)operationParameters.Target;
    }

    /// <summary>
    /// Creates a new Unreal parameter object so Unreal-specific engine resolution remains available during option
    /// discovery and child operation flows.
    /// </summary>
    protected override global::LocalAutomation.Runtime.OperationParameters CreateOperationParameters()
    {
        return new UnrealAutomationCommon.Operations.OperationParameters();
    }
}
