using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
     /// Gets whether the operation is currently executing.
     /// </summary>
    public bool Executing { get; private set; }

    /// <summary>
    /// Gets whether the operation was cancelled.
    /// </summary>
    public bool Cancelled { get; private set; }

    /// <summary>
    /// Gets the logger used while the current execution is active.
    /// </summary>
    protected ILogger Logger { get; private set; } = NullLogger.Instance;

    /// <summary>
    /// Gets the parameter state used by the current execution.
    /// </summary>
    protected OperationParameters OperationParameters { get; private set; } = null!;

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
            parameters.AdditionalArguments = existing.AdditionalArguments;

            // Clone from a stable snapshot because some option setters update sibling option state or target-derived
            // values as they are copied, which can mutate the live binding list during enumeration.
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
    /// Executes the operation on a thread-pool worker so UI callers remain responsive.
    /// </summary>
    public async Task<OperationResult> ExecuteOnThread(OperationParameters operationParameters, ILogger logger, CancellationToken token)
    {
        TaskCompletionSource<OperationResult> tcs = new();
        ThreadPool.QueueUserWorkItem(async _ =>
        {
            try
            {
                OperationResult result = await Execute(operationParameters, logger, token).ConfigureAwait(false);
                tcs.SetResult(result);
            }
            catch (Exception e)
            {
                logger.LogCritical($"Worker thread encountered exception:\n{e}");
                tcs.SetResult(new OperationResult(false));
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the operation against the provided parameter state.
    /// </summary>
    public async Task<OperationResult> Execute(OperationParameters operationParameters, ILogger logger, CancellationToken token)
    {
        try
        {
            using IDisposable operationTimingScope = logger.BeginSection(OperationName);

            int warnings = 0;
            int errors = 0;

            EventStreamLogger eventLogger = new();
            Logger = eventLogger;
            eventLogger.Output += (level, output) =>
            {
                if (level >= LogLevel.Error)
                {
                    errors++;
                }
                else if (level == LogLevel.Warning)
                {
                    warnings++;
                }

                logger.Log(level, output);
            };

            string? requirementsError = CheckRequirementsSatisfied(operationParameters);
            if (requirementsError != null)
            {
                Logger.LogError(requirementsError);
                return new OperationResult(false);
            }

            OperationParameters = operationParameters;
            Executing = true;
            Task<OperationResult> mainTask = OnExecuted(token);
            Executing = false;

            OperationResult result = await mainTask.ConfigureAwait(false);

            if (Cancelled)
            {
                Logger.LogWarning($"Operation '{OperationName}' terminated by user");
            }
            else
            {
                Logger.LogInformation($"Operation '{OperationName}' completed - {errors} error(s), {warnings} warning(s)");
            }

            if (result.Success)
            {
                if (errors > 0)
                {
                    Logger.LogError($"{errors} error(s) encountered");
                    result.Success = false;
                }

                if (warnings > 0)
                {
                    Logger.LogWarning($"{warnings} warning(s) encountered");
                    if (FailOnWarning())
                    {
                        Logger.LogError("Operation fails on warnings");
                        result.Success = false;
                    }
                }
            }

            return result;
        }
        catch (Exception e)
        {
            Executing = false;
            throw new Exception($"Exception encountered running operation '{OperationName}'", e);
        }
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
    /// Returns the option-set types required for the provided target.
    /// </summary>
    public virtual HashSet<Type> GetRequiredOptionSetTypes(IOperationTarget target)
    {
        if (target == null)
        {
            return new HashSet<Type>();
        }

        HashSet<Type> result = new();
        OperationParameters dummyParams = CreateOperationParameters();
        dummyParams.Target = target;

        if (!RequirementsSatisfied(dummyParams))
        {
            return new HashSet<Type>();
        }

        BuildCommands(dummyParams);
        foreach (OperationOptions options in dummyParams.OptionsInstances)
        {
            result.Add(options.GetType());
        }

        return result;
    }

    /// <summary>
    /// Returns whether warnings should fail the operation.
    /// </summary>
    protected virtual bool FailOnWarning()
    {
        return false;
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
    /// Returns whether execution output should be tailed from a generated log file.
    /// </summary>
    public virtual bool ShouldReadOutputFromLogFile()
    {
        return false;
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
    /// Gives derived runtimes a chance to prepare output folders or emit pre-run notices before execution starts.
    /// </summary>
    public virtual void PrepareForExecution(OperationParameters operationParameters, ILogger logger)
    {
    }

    /// <summary>
    /// Signals that the operation has been cancelled.
    /// </summary>
    protected void SetCancelled()
    {
        Cancelled = true;
    }

    /// <summary>
    /// Creates a parameter object suitable for option discovery for this operation runtime.
    /// </summary>
    protected virtual OperationParameters CreateOperationParameters()
    {
        return new OperationParameters();
    }

    /// <summary>
    /// Runs the operation-specific execution path.
    /// </summary>
    protected abstract Task<OperationResult> OnExecuted(CancellationToken token);

    /// <summary>
    /// Builds the command list for the provided operation parameters.
    /// </summary>
    protected abstract IEnumerable<Command> BuildCommands(OperationParameters operationParameters);

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
}
