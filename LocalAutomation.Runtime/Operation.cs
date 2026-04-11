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
        if (existing != null)
        {
            parameters.Target = existing.Target;
            parameters.OutputPathOverride = existing.OutputPathOverride;

            /* Clone from a stable snapshot because some option setters update sibling option state or target-derived
               values as they are copied, which can mutate the live binding list during enumeration. Parameters act as a
               bag of values, so child operations inherit the full option state and enforce declarations through the
               operation-owned accessor instead of parameter-owned whitelists. */
            foreach (OperationOptions options in existing.OptionsInstances.ToList())
            {
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
        if (!RequirementsSatisfied(operationParameters))
        {
            return new List<Command>();
        }

        return BuildCommands(ValidateParameters(operationParameters));
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
        return Path.Combine(OutputPaths.TempRoot(), ExecutionPathConventions.MakeCompactSegment(OperationName));
    }

    /// <summary>
    /// Returns the temporary directory used by the provided execution task, isolating live session-backed runs from one
    /// another while keeping preview-time path resolution stable.
    /// </summary>
    public string GetOperationTempPath(ExecutionTaskContext context)
    {
        if (context == null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        return context.TryGetSessionId(out ExecutionSessionId sessionId)
            ? Path.Combine(OutputPaths.GetSessionTempRoot(sessionId), ExecutionPathConventions.MakeCompactSegment(OperationName))
            : GetOperationTempPath();
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
    /// Returns the default output path for the provided validated operation parameters.
    /// </summary>
    public string GetOutputPath(ValidatedOperationParameters operationParameters)
    {
        if (operationParameters.OutputPathOverride != null)
        {
            return operationParameters.OutputPathOverride;
        }

        return Path.Combine(operationParameters.Target.OutputDirectory, OperationName.Replace(" ", string.Empty));
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
    /// Declares any exclusive locks the runtime should hold while this operation's task body is executing.
    /// </summary>
    protected virtual IEnumerable<ExecutionLock> GetExecutionLocks(ValidatedOperationParameters operationParameters)
    {
        return Array.Empty<ExecutionLock>();
    }

    /// <summary>
    /// Exposes the warning-failure policy to the framework-owned execution pipeline without making callers subclass this
    /// type just to inspect the configured behavior.
    /// </summary>
    internal bool ShouldFailOnWarning()
    {
        return FailOnWarning();
    }

    internal IReadOnlyList<ExecutionLock> GetDeclaredExecutionLocks(ValidatedOperationParameters operationParameters)
    {
        return GetExecutionLocks(operationParameters)
            .Where(executionLock => executionLock != null)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Executes one nested child operation by attaching the same descendant subtree shape that static expansion would
    /// produce beneath the current task, then waiting for that attached subtree to finish.
    /// </summary>
    protected async Task<OperationResult> RunChildOperationAsync(Operation childOperation, OperationParameters operationParameters, ExecutionTaskContext context, bool required = false, string? failureMessage = null, bool hideChildOperationRootInGraph = false)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("Operation.RunChildOperationAsync")
            .SetTag("parent.operation", OperationName)
            .SetTag("child.operation", childOperation.OperationName)
            .SetTag("required", required)
            .SetTag("target.type", operationParameters.Target?.GetType().Name ?? string.Empty)
            .SetTag("child.root.hidden", hideChildOperationRootInGraph);

        OperationResult result = await context.RunChildOperationAsync(childOperation, operationParameters, hideChildOperationRootInGraph).ConfigureAwait(false);
        activity.SetTag("result.outcome", result.Outcome.ToString())
            .SetTag("result.success", result.Success);
        context.Logger.LogDebug(
            "Task '{TaskTitle}' received child operation '{ChildOperation}' result '{Outcome}' (required={Required}) with reason '{FailureReason}'.",
            context.Title,
            childOperation.OperationName,
            result.Outcome,
            required,
            result.FailureReason ?? string.Empty);
        if (!required || result.Success)
        {
            return result;
        }

        if (result.Outcome == ExecutionTaskOutcome.Cancelled)
        {
            activity.SetTag("result.transition", "ThrowCancelled");
            throw new OperationCanceledException(context.CancellationToken);
        }

        if (result.Outcome == ExecutionTaskOutcome.Interrupted)
        {
            /* Interrupted child operations currently propagate as a normal result instead of an exception. Log that branch
               explicitly so task-body methods that only await the child run still leave a trace in the launch log. */
            context.Logger.LogWarning(
                "Task '{TaskTitle}' is returning interrupted child result from '{ChildOperation}' without throwing. FailureReason='{FailureReason}'.",
                context.Title,
                childOperation.OperationName,
                result.FailureReason ?? string.Empty);
            activity.SetTag("result.transition", "ReturnInterrupted");
            return result;
        }

        string childFailureReason = string.IsNullOrWhiteSpace(result.FailureReason)
            ? $"Child operation '{childOperation.OperationName}' failed."
            : result.FailureReason;
        string effectiveFailureMessage = string.IsNullOrWhiteSpace(failureMessage)
            ? childFailureReason
            : $"{failureMessage}{Environment.NewLine}Cause: {childFailureReason}";

        /* Required child operations should keep the caller's framing while still preserving the concrete child failure
           reason that explains why the nested operation returned Failed. */
        activity.SetTag("result.transition", "ThrowFailure")
            .SetTag("failure.message", effectiveFailureMessage)
            .SetTag("child.failure.reason", childFailureReason);
        throw new Exception(effectiveFailureMessage);
    }

    /// <summary>
    /// Executes one nested child operation by type when the caller does not need to reuse a specific instance.
    /// </summary>
    protected Task<OperationResult> RunChildOperationAsync<TOperation>(OperationParameters operationParameters, ExecutionTaskContext context, bool required = false, string? failureMessage = null, bool hideChildOperationRootInGraph = false)
        where TOperation : Operation, new()
    {
        return RunChildOperationAsync(new TOperation(), operationParameters, context, required, failureMessage, hideChildOperationRootInGraph);
    }

    /// <summary>
    /// Validates the current parameter state and returns an error message when the operation cannot run.
    /// </summary>
    public virtual string? CheckRequirementsSatisfied(OperationParameters operationParameters)
    {
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

        return CheckRequirementsSatisfied(ValidateParameters(operationParameters));
    }

    /// <summary>
    /// Validates operation-specific option usage after the shared target checks have passed.
    /// </summary>
    protected virtual string? CheckRequirementsSatisfied(ValidatedOperationParameters operationParameters)
    {
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
    /// Returns the current target from the validated parameter view.
    /// </summary>
    public IOperationTarget GetTarget(ValidatedOperationParameters operationParameters)
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
    /// Gives the framework one internal entrypoint for plan authoring while keeping the override surface purely
    /// protected for derived operations. This avoids leaking framework invocation visibility into external subclasses.
    /// </summary>
    internal void AuthorExecutionPlan(ValidatedOperationParameters operationParameters, ExecutionTaskBuilder root)
    {
        DescribeExecutionPlan(operationParameters, root);
    }

    /// <summary>
    /// Lets derived operations describe child tasks beneath the framework-owned root task.
    /// </summary>
    protected virtual void DescribeExecutionPlan(ValidatedOperationParameters operationParameters, ExecutionTaskBuilder root)
    {
        throw new NotSupportedException($"Operation '{OperationName}' must override {nameof(DescribeExecutionPlan)}.");
    }

    /// <summary>
    /// Builds the command list for the provided operation parameters.
    /// </summary>
    protected virtual IEnumerable<Command> BuildCommands(ValidatedOperationParameters operationParameters)
    {
        return Enumerable.Empty<Command>();
    }

    /// <summary>
    /// Creates the operation-scoped validated view over one raw parameter bag.
    /// </summary>
    protected internal ValidatedOperationParameters ValidateParameters(OperationParameters operationParameters)
    {
        return new ValidatedOperationParameters(this, operationParameters);
    }

    /// <summary>
    /// Returns the user-facing operation name.
    /// </summary>
    protected virtual string GetOperationName()
    {
        return SplitWordsByUppercase(GetType().Name);
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

    /// <summary>
    /// Returns the current target cast to the required target type from the validated parameter view.
    /// </summary>
    public new T GetTarget(ValidatedOperationParameters operationParameters)
    {
        return operationParameters.GetTarget<T>();
    }

    /// <summary>
    /// Returns the current target or throws when execution reaches a path that depends on prior validation.
    /// </summary>
    protected T GetRequiredTarget(ValidatedOperationParameters operationParameters)
    {
        T? target = GetTarget(operationParameters);
        return target ?? throw new InvalidOperationException($"Operation {GetType().Name} requires a target of type {typeof(T).Name}.");
    }
}
