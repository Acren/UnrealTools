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
/// Builds authored execution plans and runs them through the shared scheduler so operations only describe task
/// structure and task behavior while the framework owns orchestration.
/// </summary>
public sealed class Runner
{
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly OperationParameters _operationParameters;
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();
    private Task<OperationResult>? _currentTask;
    private ILogger _logger = NullLogger.Instance;

    /// <summary>
    /// Creates a runtime runner for the provided operation and parameter state.
    /// </summary>
    public Runner(Operation operation, OperationParameters operationParameters)
    {
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        _operationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
    }

    /// <summary>
    /// Gets whether the runner currently has an active scheduler task.
    /// </summary>
    public bool IsRunning => _currentTask is { IsCompleted: false };

    /// <summary>
    /// Gets the operation whose plan this runner owns.
    /// </summary>
    public Operation Operation { get; }

    /// <summary>
    /// Builds the preview/runtime plan for the current operation using the same framework-owned authoring path that
    /// execution uses.
    /// </summary>
    public ExecutionPlan? BuildPlan()
    {
        return BuildPlan(Operation, _operationParameters);
    }

    /// <summary>
    /// Builds the preview/runtime plan for any operation using the shared framework-owned authoring pipeline.
    /// </summary>
    public static ExecutionPlan? BuildPlan(Operation operation, OperationParameters operationParameters)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (operationParameters == null)
        {
            throw new ArgumentNullException(nameof(operationParameters));
        }

        operation.PrepareRegisteredOptions(operationParameters);
        if (operationParameters.Target == null)
        {
            return null;
        }

        return BuildWrappedPlan(operation, operationParameters, NullLogger.Instance);
    }

    /// <summary>
    /// Executes the current operation by building its plan, wrapping task callbacks with framework-owned execution
    /// context, and then running that plan through the shared scheduler.
    /// </summary>
    public async Task<OperationResult> Run(ExecutionPlan plan, ExecutionSession? session = null)
    {
        if (_currentTask != null)
        {
            throw new InvalidOperationException("Task is already running");
        }

        _logger = ResolveCurrentLogger();

        string outputPath = Operation.GetOutputPath(_operationParameters);
        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }

        EventStreamLogger eventLogger = CreateAggregatingLogger();
        string? requirementsError = Operation.CheckRequirementsSatisfied(_operationParameters);
        if (requirementsError != null)
        {
            _logger.LogError(requirementsError);
            return OperationResult.Failed();
        }

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("Runner.Run")
            .SetTag("operation.name", Operation.OperationName)
            .SetTag("plan.id", plan.Id.Value)
            .SetTag("plan.task.count", plan.Tasks.Count);
        using IDisposable operationTimingScope = eventLogger.BeginSection(Operation.OperationName);
        _currentTask = ExecuteOnThread(() => new ExecutionPlanScheduler(eventLogger, maxParallelism: 1, session).ExecuteAsync(plan, _cancellationTokenSource.Token));
        try
        {
            OperationResult result = await _currentTask.ConfigureAwait(false);
            activity.SetTag("scheduler.result", result.Outcome.ToString());
            using PerformanceActivityScope finalizeActivity = PerformanceTelemetry.StartActivity("Runner.Run.FinalizeOutcome")
                .SetTag("operation.name", Operation.OperationName)
                .SetTag("incoming.result", result.Outcome.ToString());
            return FinalizeOutcome(result, eventLogger);
        }
        finally
        {
            _logger.LogDebug("'{OperationName}' task ended", Operation.OperationName);
            _currentTask = null;
        }
    }

    /// <summary>
    /// Executes one nested child operation by merging its descendant tasks into the current live session graph beneath
    /// the current task and then re-entering the same root scheduler until that inserted child work finishes.
    /// </summary>
    public static async Task<OperationResult> RunChildOperation(Operation operation, OperationParameters operationParameters, ExecutionTaskContext parentContext)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (operationParameters == null)
        {
            throw new ArgumentNullException(nameof(operationParameters));
        }

        if (parentContext == null)
        {
            throw new ArgumentNullException(nameof(parentContext));
        }

        if (parentContext.Session == null)
        {
            throw new InvalidOperationException($"Cannot run child operation '{operation.OperationName}' without a live execution session.");
        }

        if (parentContext.Scheduler == null)
        {
            throw new InvalidOperationException($"Cannot run child operation '{operation.OperationName}' without an active execution scheduler.");
        }

        ExecutionPlan childPlan = BuildWrappedPlan(operation, operationParameters, parentContext.Logger)
            ?? throw new InvalidOperationException($"Operation '{operation.OperationName}' did not produce an execution plan.");
        ChildTaskMergeResult mergeResult = parentContext.Session.MergeChildTasks(parentContext.TaskId, childPlan);
        return await parentContext.Scheduler.WaitForInsertedChildTasksAsync(operation, parentContext, mergeResult).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the current operation and waits for the scheduler to stop.
    /// </summary>
    public async Task Cancel()
    {
        Task<OperationResult>? currentTask = _currentTask;
        if (currentTask == null)
        {
            return;
        }

        _logger.LogWarning("Cancelling operation '{OperationName}'", Operation.OperationName);
        _cancellationTokenSource.Cancel();
        await currentTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Builds one operation plan and wraps every executable task callback so task execution happens inside a
    /// framework-owned operation execution context.
    /// </summary>
    private static ExecutionPlan? BuildWrappedPlan(Operation operation, OperationParameters operationParameters, ILogger logger)
    {
        operation.PrepareRegisteredOptions(operationParameters);
        if (operationParameters.Target == null)
        {
            return null;
        }

        ExecutionPlanId planId = ExecutionIdentifierFactory.CreatePlanId(operation.GetType().Name);
        ExecutionPlanBuilder builder = new(operation.OperationName, planId, (childOperation, childParameters) => BuildWrappedPlan(childOperation, childParameters, logger));
        builder.SetBuilderOperationParameters(operationParameters);
        ExecutionTaskBuilder root = builder.Task(operation.OperationName, operationParameters.Target.DisplayName, default);
        operation.DescribeExecutionPlan(operationParameters, root);
        return builder.BuildPlan();
    }

    /// <summary>
    /// Executes one delegate on a worker thread so UI-triggered runs remain asynchronous even though the framework now
    /// owns the plan lifecycle instead of the operation instance.
    /// </summary>
    private static Task<OperationResult> ExecuteOnThread(Func<Task<OperationResult>> executeAsync)
    {
        TaskCompletionSource<OperationResult> completionSource = new();
        ThreadPool.QueueUserWorkItem(async _ =>
        {
            try
            {
                completionSource.SetResult(await executeAsync().ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                completionSource.SetResult(OperationResult.Cancelled());
            }
            catch (Exception ex)
            {
                completionSource.SetException(ex);
            }
        });

        return completionSource.Task;
    }

    /// <summary>
    /// Creates the aggregate logger used during one top-level run so warning/error counting and task-state forwarding
    /// continue to work after plan ownership moved out of operations.
    /// </summary>
    private EventStreamLogger CreateAggregatingLogger()
    {
        return CreateAggregatingLogger(
            _logger,
            _logger as IExecutionTaskLoggerFactory,
            _logger as IExecutionTaskStateSink,
            (level, output) =>
            {
                if (level >= LogLevel.Error)
                {
                    _errors.Add(output);
                }
                else if (level == LogLevel.Warning)
                {
                    _warnings.Add(output);
                }

                _logger.Log(level, output);
            });
    }

    /// <summary>
    /// Creates an event-stream logger that forwards formatted output to the supplied sink while preserving task logging
    /// and task-state routing when the host logger supports them.
    /// </summary>
    private static EventStreamLogger CreateAggregatingLogger(ILogger fallbackLogger, IExecutionTaskLoggerFactory? taskLoggerFactory, IExecutionTaskStateSink? taskStateSink, Action<LogLevel, string>? onOutput)
    {
        EventStreamLogger eventLogger = new(taskLoggerFactory ?? fallbackLogger as IExecutionTaskLoggerFactory, taskStateSink ?? fallbackLogger as IExecutionTaskStateSink);
        if (onOutput != null)
        {
            eventLogger.Output += (level, output) =>
            {
                if (output == null)
                {
                    throw new InvalidOperationException("Null line");
                }

                onOutput(level, output);
            };
        }

        return eventLogger;
    }

    /// <summary>
    /// Applies the historical operation-level warning and error rollup semantics after the scheduler has finished.
    /// </summary>
    private OperationResult FinalizeOutcome(OperationResult result, ILogger logger)
    {
        return FinalizeOutcome(Operation, result, logger, _logger, _warnings.Count, _errors.Count);
    }

    /// <summary>
    /// Applies the historical operation-level warning and error rollup semantics for both top-level and nested runs.
    /// </summary>
    private static OperationResult FinalizeOutcome(Operation operation, OperationResult result, ILogger aggregateLogger, ILogger summaryLogger, int warningCount, int errorCount)
    {
        if (result.Outcome == RunOutcome.Cancelled)
        {
            aggregateLogger.LogWarning("Operation '{OperationName}' terminated by user", operation.OperationName);
        }
        else if (result.Outcome == RunOutcome.Succeeded)
        {
            aggregateLogger.LogInformation("Operation '{OperationName}' completed successfully - {ErrorCount} error(s), {WarningCount} warning(s)", operation.OperationName, errorCount, warningCount);
        }
        else
        {
            aggregateLogger.LogWarning("Operation '{OperationName}' finished with failure - {ErrorCount} error(s), {WarningCount} warning(s)", operation.OperationName, errorCount, warningCount);
        }

        if (result.Outcome == RunOutcome.Succeeded)
        {
            if (errorCount > 0)
            {
                aggregateLogger.LogError("{ErrorCount} error(s) encountered", errorCount);
                result.Outcome = RunOutcome.Failed;
            }

            if (warningCount > 0)
            {
                aggregateLogger.LogWarning("{WarningCount} warning(s) encountered", warningCount);
                if (operation.ShouldFailOnWarning())
                {
                    aggregateLogger.LogError("Operation fails on warnings");
                    result.Outcome = RunOutcome.Failed;
                }
            }
        }

        if (result.Outcome == RunOutcome.Succeeded)
        {
            if (warningCount > 0)
            {
                int numToShow = Math.Min(warningCount, 50);
                summaryLogger.LogWarning("{ShownCount} of {WarningCount} warnings:", numToShow, warningCount);
            }

            if (errorCount > 0)
            {
                int numToShow = Math.Min(errorCount, 50);
                summaryLogger.LogWarning("{ShownCount} of {ErrorCount} errors:", numToShow, errorCount);
            }

            summaryLogger.LogInformation("'{OperationName}' finished with result: success", operation.OperationName);
        }
        else if (result.Outcome == RunOutcome.Cancelled)
        {
            summaryLogger.LogWarning("'{OperationName}' finished with result: cancelled", operation.OperationName);
        }
        else
        {
            summaryLogger.LogError("'{OperationName}' finished with result: failure", operation.OperationName);
        }

        return result;
    }

    /// <summary>
    /// Resolves the active host logger at run time so session-specific forwarding loggers can be installed before the
    /// framework-owned execution path begins.
    /// </summary>
    private static ILogger ResolveCurrentLogger()
    {
        try
        {
            return ApplicationLogger.Logger;
        }
        catch (InvalidOperationException)
        {
            return NullLogger.Instance;
        }
    }
}
