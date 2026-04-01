using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides the generic runtime behavior for discovering, validating, previewing, and executing operations.
/// </summary>
public abstract class Operation
{
    /// <summary>
    /// Gets the user-facing operation name.
    /// </summary>
    public string OperationName => GetOperationName();

    /// <summary>
    /// Creates a runtime operation instance for the provided type.
    /// </summary>
    public static Operation CreateOperation(Type operationType)
    {
        if (operationType == null)
        {
            throw new ArgumentNullException(nameof(operationType));
        }

        return (Operation)Activator.CreateInstance(operationType)!;
    }

    /// <summary>
    /// Returns whether the provided operation type supports the supplied target instance.
    /// </summary>
    public static bool OperationTypeSupportsTarget(Type operationType, IOperationTarget target)
    {
        if (operationType == null)
        {
            return false;
        }

        return CreateOperation(operationType).SupportsTarget(target);
    }

    /// <summary>
    /// Creates parameter state for this operation, optionally cloning shared state from an existing parameter set.
    /// </summary>
    public virtual OperationParameters CreateParameters(OperationParameters? existing = null)
    {
        OperationParameters parameters = CreateOperationParameters();
        // Option discovery can run before a target has been selected, so only ask the operation for target-specific
        // option sets once a concrete target exists.
        IOperationTarget? optionTarget = existing?.Target ?? parameters.Target;
        parameters.SetRegisteredOptions(optionTarget != null ? GetRequiredOptionSetTypes(optionTarget) : Array.Empty<Type>());
        if (existing != null)
        {
            parameters.Target = existing.Target;
            parameters.OutputPathOverride = existing.OutputPathOverride;
            parameters.AdditionalArguments = existing.AdditionalArguments;

            // Clone from a stable snapshot because some option setters update sibling option state or target-derived
            // values as they are copied, which can mutate the live binding list during enumeration.
            foreach (OperationOptions options in existing.OptionsInstances.ToList())
            {
                if (!parameters.IsOptionRegistered(options.GetType()))
                {
                    continue;
                }

                if (parameters.GetOptionsInstance(options.GetType()) == null)
                {
                    parameters.SetOptions((OperationOptions)options.Clone());
                }
            }
        }

        return parameters;
    }

    /// <summary>
    /// Returns the formatted command previews for the provided parameter state.
    /// </summary>
    public IEnumerable<Command> GetCommands(OperationParameters operationParameters)
    {
        PrepareRegisteredOptions(operationParameters);
        if (!RequirementsSatisfied(operationParameters))
        {
            return new List<Command>();
        }

        return BuildCommands(operationParameters);
    }

    /// <summary>
    /// Returns formatted command preview strings for the provided parameter state.
    /// </summary>
    public virtual IReadOnlyList<string> GetCommandTexts(OperationParameters operationParameters)
    {
        return GetCommands(operationParameters).Select(command => command.ToString()).ToList();
    }

    /// <summary>
    /// Returns the shared temporary directory used by operation flows.
    /// </summary>
    public string GetOperationTempPath()
    {
        return Path.Combine(OutputPaths.Root(), "Temp");
    }

    /// <summary>
    /// Returns the default output path for the provided operation parameters.
    /// </summary>
    public string GetOutputPath(OperationParameters operationParameters)
    {
        if (operationParameters.OutputPathOverride != null)
        {
            return operationParameters.OutputPathOverride;
        }

        return Path.Combine(operationParameters.Target!.OutputDirectory, OperationName.Replace(" ", string.Empty));
    }

    /// <summary>
    /// Returns the option-set types required for the provided target using explicit metadata rather than command or
    /// validation side effects.
    /// </summary>
    public virtual IReadOnlyCollection<Type> GetRequiredOptionSetTypes(IOperationTarget target)
    {
        if (target == null || !SupportsTarget(target))
        {
            return Array.Empty<Type>();
        }

        return GetDeclaredOptionSetTypes(target)
            .Where(type => type != null)
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Declares the option-set types this operation exposes for the provided target.
    /// </summary>
    protected virtual IEnumerable<Type> GetDeclaredOptionSetTypes(IOperationTarget target)
    {
        return Array.Empty<Type>();
    }

    /// <summary>
    /// Returns whether warnings should fail the operation.
    /// </summary>
    protected virtual bool FailOnWarning()
    {
        return false;
    }

    /// <summary>
    /// Exposes the warning-failure policy to the framework-owned execution pipeline without making callers subclass this
    /// type just to inspect the configured behavior.
    /// </summary>
    internal bool ShouldFailOnWarning()
    {
        return FailOnWarning();
    }

    /// <summary>
    /// Executes one nested child operation through the framework-owned plan pipeline while preserving the caller's logger
    /// and cancellation token.
    /// </summary>
    protected async Task<OperationResult> RunChildOperationAsync(Operation childOperation, OperationParameters operationParameters, ILogger logger, CancellationToken cancellationToken, bool required = false, string? failureMessage = null)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("Operation.RunChildOperationAsync")
            .SetTag("parent.operation", OperationName)
            .SetTag("child.operation", childOperation.OperationName)
            .SetTag("required", required)
            .SetTag("target.type", operationParameters.Target?.GetType().Name ?? string.Empty);

        OperationResult result = await Runner.RunChildOperation(childOperation, operationParameters, logger, cancellationToken);
        activity.SetTag("result.outcome", result.Outcome.ToString())
            .SetTag("result.success", result.Success);
        if (!required || result.Success)
        {
            return result;
        }

        if (result.Outcome == RunOutcome.Cancelled)
        {
            activity.SetTag("result.transition", "ThrowCancelled");
            throw new OperationCanceledException(cancellationToken);
        }

        activity.SetTag("result.transition", "ThrowFailure")
            .SetTag("failure.message", failureMessage ?? $"Child operation '{childOperation.OperationName}' failed.");
        throw new Exception(failureMessage ?? $"Child operation '{childOperation.OperationName}' failed.");
    }

    /// <summary>
    /// Executes one nested child operation by type when the caller does not need to reuse a specific instance.
    /// </summary>
    protected Task<OperationResult> RunChildOperationAsync<TOperation>(OperationParameters operationParameters, ILogger logger, CancellationToken cancellationToken, bool required = false, string? failureMessage = null)
        where TOperation : Operation, new()
    {
        return RunChildOperationAsync(new TOperation(), operationParameters, logger, cancellationToken, required, failureMessage);
    }

    /// <summary>
    /// Validates the current parameter state and returns an error message when the operation cannot run.
    /// </summary>
    public virtual string? CheckRequirementsSatisfied(OperationParameters operationParameters)
    {
        PrepareRegisteredOptions(operationParameters);
        if (operationParameters.Target == null)
        {
            return "Target not specified";
        }

        if (!SupportsTarget(operationParameters.Target))
        {
            return $"Target {operationParameters.Target.Name} of type {operationParameters.Target.GetType()} is not supported";
        }

        if (!operationParameters.Target.IsValid)
        {
            return $"Target {operationParameters.Target.Name} of type {operationParameters.Target.GetType()} is not valid";
        }

        return null;
    }

    /// <summary>
    /// Returns whether the current parameter state is valid.
    /// </summary>
    public bool RequirementsSatisfied(OperationParameters operationParameters)
    {
        return CheckRequirementsSatisfied(operationParameters) == null;
    }

    /// <summary>
    /// Returns whether the operation supports the provided target instance.
    /// </summary>
    public abstract bool SupportsTarget(IOperationTarget target);

    /// <summary>
    /// Returns the current target from the parameter state.
    /// </summary>
    public IOperationTarget? GetTarget(OperationParameters operationParameters)
    {
        return operationParameters.Target;
    }

    /// <summary>
    /// Returns the log directory used by the operation when one exists.
    /// </summary>
    public virtual string? GetLogsPath(OperationParameters operationParameters)
    {
        return null;
    }

    /// <summary>
    /// Creates a parameter object suitable for option discovery for this operation runtime.
    /// </summary>
    protected virtual OperationParameters CreateOperationParameters()
    {
        return new OperationParameters();
    }

    /// <summary>
     /// Lets derived operations describe child tasks beneath the framework-owned root task.
     /// </summary>
    protected internal virtual void DescribeExecutionPlan(OperationParameters operationParameters, ExecutionTaskBuilder root)
    {
        throw new NotSupportedException($"Operation '{OperationName}' must override {nameof(DescribeExecutionPlan)}.");
    }

    /// <summary>
    /// Builds the command list for the provided operation parameters.
    /// </summary>
    protected virtual IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
    {
        return Enumerable.Empty<Command>();
    }

    /// <summary>
    /// Returns the user-facing operation name.
    /// </summary>
    protected virtual string GetOperationName()
    {
        return SplitWordsByUppercase(GetType().Name);
    }

    /// <summary>
    /// Recomputes the registered option-set list for the current target before validation, preview, or execution touch
    /// option values directly.
    /// </summary>
    /// <summary>
    /// Recomputes the registered option-set list before command preview, validation, plan authoring, or execution so
    /// the current target always drives the live option shape.
    /// </summary>
    internal void PrepareRegisteredOptions(OperationParameters operationParameters)
    {
        if (operationParameters == null)
        {
            throw new ArgumentNullException(nameof(operationParameters));
        }

        operationParameters.SetRegisteredOptions(GetRequiredOptionSetTypes(operationParameters.Target!));
    }

    /// <summary>
    /// Expands PascalCase identifiers into space-separated words for display.
    /// </summary>
    private static string SplitWordsByUppercase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        System.Text.StringBuilder builder = new();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 && char.IsUpper(current))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Restricts an operation to a specific target type while reusing the shared runtime behavior.
/// </summary>
public abstract class Operation<T> : Operation where T : IOperationTarget
{
    /// <summary>
    /// Creates a typed runtime operation instance for the provided type.
    /// </summary>
    public new static Operation<T> CreateOperation(Type operationType)
    {
        return (Operation<T>)Activator.CreateInstance(operationType)!;
    }

    /// <summary>
    /// Returns whether the provided target matches the required target type.
    /// </summary>
    public override bool SupportsTarget(IOperationTarget target)
    {
        return target is T;
    }

    /// <summary>
    /// Returns the current target cast to the required target type.
    /// </summary>
    public new T? GetTarget(OperationParameters operationParameters)
    {
        return (T?)operationParameters.Target;
    }
}
