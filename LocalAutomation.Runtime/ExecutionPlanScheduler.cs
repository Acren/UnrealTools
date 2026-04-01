using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Executes declared work items from a composite execution plan, honoring explicit dependencies and running ready items
/// in parallel up to the configured concurrency limit.
/// </summary>
public sealed class ExecutionPlanScheduler
{
    private const string WaitingForDependenciesReason = "Waiting for dependencies.";
    private const string UnsatisfiedDependenciesReason = "Scheduler could not satisfy the remaining dependencies.";

    private readonly ILogger _logger;
    private readonly IExecutionTaskStateSink? _taskStateSink;
    private readonly int _maxParallelism;

    /// <summary>
    /// Creates a scheduler that reports state transitions through the provided logger when it supports task-state
    /// routing.
    /// </summary>
    public ExecutionPlanScheduler(ILogger logger, int? maxParallelism = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _taskStateSink = logger as IExecutionTaskStateSink;
        _maxParallelism = Math.Max(1, maxParallelism ?? Environment.ProcessorCount);
    }

    /// <summary>
    /// Executes the provided authored execution plan until its runnable tasks complete, fail, are skipped because they
    /// can no longer run, or are cancelled.
    /// </summary>
    public async Task<OperationResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken)
    {
        Dictionary<ExecutionTaskId, ExecutionPlanTask> itemsById = Materialize(plan);
        Dictionary<Type, object> sharedData = new();
        Dictionary<ExecutionTaskId, ExecutionTaskStatus> statuses = new();
        Dictionary<ExecutionTaskId, string?> statusReasons = new();
        Dictionary<ExecutionTaskId, Task<OperationResult>> runningTasks = new();
        Dictionary<Task<OperationResult>, ExecutionTaskId> taskOwners = new(TaskComparer.Instance);
        bool encounteredFailure = false;
        bool encounteredCancellation = false;

        foreach (ExecutionPlanTask item in itemsById.Values)
        {
            if (item.ExecuteAsync == null)
            {
                SetStatus(statuses, statusReasons, item.Id, ExecutionTaskStatus.Pending);
                continue;
            }

            if (!item.Enabled)
            {
                SetStatus(statuses, statusReasons, item.Id, ExecutionTaskStatus.Disabled, item.DisabledReason);
                continue;
            }

            // Before a task actually starts, the design only needs to know that the task is still pending. Whether it
            // is waiting on dependencies or scheduler turn is internal scheduler detail rather than a user-facing state.
            SetStatus(statuses, statusReasons, item.Id, ExecutionTaskStatus.Pending, plan.GetTaskDependencies(item.Id).Count > 0 ? WaitingForDependenciesReason : null);
        }

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                encounteredCancellation = true;
                CancelOutstandingTasks(itemsById, statuses, statusReasons);
                break;
            }

            bool startedAny = StartReadyItems(itemsById, sharedData, statuses, statusReasons, runningTasks, taskOwners, cancellationToken);
            if (runningTasks.Count == 0)
            {
                IReadOnlyList<ExecutionTaskId> incompleteTasks = itemsById.Values
                    .Where(task => task.ExecuteAsync != null)
                    .Select(task => task.Id)
                    .Where(taskId => statuses[taskId] is not ExecutionTaskStatus.Completed and not ExecutionTaskStatus.Cancelled and not ExecutionTaskStatus.Skipped and not ExecutionTaskStatus.Disabled)
                    .ToList();
                if (incompleteTasks.Count == 0)
                {
                    break;
                }

                if (!startedAny)
                {
                    foreach (ExecutionTaskId taskId in incompleteTasks)
                    {
                        if (statuses[taskId] is ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled)
                        {
                            continue;
                        }

                        SetStatus(statuses, statusReasons, taskId, ExecutionTaskStatus.Skipped, UnsatisfiedDependenciesReason);
                    }

                    encounteredFailure = true;
                    break;
                }
            }

            if (runningTasks.Count == 0)
            {
                continue;
            }

            Task<OperationResult> completedTask = await Task.WhenAny(runningTasks.Values).ConfigureAwait(false);
            ExecutionTaskId completedTaskId = taskOwners[completedTask];
            runningTasks.Remove(completedTaskId);
            taskOwners.Remove(completedTask);
            using PerformanceActivityScope completionActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.HandleCompletedTask")
                .SetTag("task.id", completedTaskId.Value);

            OperationResult result;
            try
            {
                result = await completedTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                encounteredCancellation = true;
                SetStatus(statuses, statusReasons, completedTaskId, ExecutionTaskStatus.Cancelled, "Cancelled.");
                CancelOutstandingTasks(itemsById, statuses, statusReasons);
                UpdateReadiness(itemsById, statuses, statusReasons);
                continue;
            }
            catch (Exception ex)
            {
                encounteredFailure = true;
                SetStatus(statuses, statusReasons, completedTaskId, ExecutionTaskStatus.Failed, ex.Message);
                SkipDependentsOfFailure(completedTaskId, itemsById, statuses, statusReasons);
                UpdateReadiness(itemsById, statuses, statusReasons);
                continue;
            }

            completionActivity.SetTag("task.outcome", result.Outcome.ToString());

            using PerformanceActivityScope readinessActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.UpdateReadinessAfterCompletion")
                .SetTag("task.id", completedTaskId.Value);
            if (result.Outcome == RunOutcome.Succeeded)
            {
                SetStatus(statuses, statusReasons, completedTaskId, ExecutionTaskStatus.Completed);
            }
            else if (result.Outcome == RunOutcome.Cancelled)
            {
                encounteredCancellation = true;
                SetStatus(statuses, statusReasons, completedTaskId, ExecutionTaskStatus.Cancelled, "Cancelled.");
                CancelOutstandingTasks(itemsById, statuses, statusReasons);
            }
            else
            {
                encounteredFailure = true;
                SetStatus(statuses, statusReasons, completedTaskId, ExecutionTaskStatus.Failed, "The task execution callback returned failure.");
                SkipDependentsOfFailure(completedTaskId, itemsById, statuses, statusReasons);
            }

            UpdateReadiness(itemsById, statuses, statusReasons);
        }

        bool anyFailures = encounteredFailure || statuses.Values.Any(status => status is ExecutionTaskStatus.Failed);
        if (anyFailures)
        {
            return OperationResult.Failed();
        }

        bool anyCancelled = encounteredCancellation || statuses.Values.Any(status => status == ExecutionTaskStatus.Cancelled);
        if (anyCancelled)
        {
            return OperationResult.Cancelled();
        }

        return OperationResult.Succeeded();
    }

    /// <summary>
    /// Materializes and validates the declared executable task set before execution begins.
    /// </summary>
    private static Dictionary<ExecutionTaskId, ExecutionPlanTask> Materialize(ExecutionPlan plan)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        Dictionary<ExecutionTaskId, ExecutionPlanTask> itemsById = new();
        foreach (ExecutionPlanTask item in plan.Tasks)
        {
            if (!itemsById.TryAdd(item.Id, item))
            {
                throw new InvalidOperationException($"Execution scheduler contains duplicate task '{item.Id}'.");
            }
        }

        foreach (ExecutionPlanTask item in itemsById.Values.Where(task => task.ExecuteAsync != null))
        {
            foreach (ExecutionTaskId dependencyId in plan.GetTaskDependencies(item.Id))
            {
                if (!itemsById.ContainsKey(dependencyId))
                {
                    throw new InvalidOperationException($"Execution task '{item.Id}' depends on unknown task '{dependencyId}'.");
                }
            }
        }

        return itemsById;
    }

    /// <summary>
    /// Starts any ready work items while scheduler capacity remains available.
    /// </summary>
    private bool StartReadyItems(
        IReadOnlyDictionary<ExecutionTaskId, ExecutionPlanTask> itemsById,
        IDictionary<Type, object> sharedData,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons,
        IDictionary<ExecutionTaskId, Task<OperationResult>> runningTasks,
        IDictionary<Task<OperationResult>, ExecutionTaskId> taskOwners,
        CancellationToken cancellationToken)
    {
        bool startedAny = false;
        foreach (ExecutionPlanTask item in itemsById.Values.Where(item => item.ExecuteAsync != null && statuses[item.Id] == ExecutionTaskStatus.Pending && GetTaskDependencies(item.Id, itemsById).All(dependencyId => IsDependencySatisfied(dependencyId, itemsById, statuses, new HashSet<ExecutionTaskId>()))).OrderBy(item => item.Id.Value, StringComparer.Ordinal))
        {
            if (runningTasks.Count >= _maxParallelism)
            {
                break;
            }

            using PerformanceActivityScope startActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.StartTask")
                .SetTag("task.id", item.Id.Value)
                .SetTag("task.title", item.Title);
            SetStatus(statuses, statusReasons, item.Id, ExecutionTaskStatus.Running);
            ExecutionTaskContext context = new(item.Id, item.Title, CreateTaskLogger(item.Id), cancellationToken, item.OperationParameters, sharedData);
            Task<OperationResult> task = ExecuteTaskBodyAsync(item, context);
            runningTasks[item.Id] = task;
            taskOwners[task] = item.Id;
            startedAny = true;
        }

        return startedAny;
    }

    /// <summary>
    /// Recomputes which pending items remain eligible to run. Tasks stay pending until they either start running or are
    /// marked terminal because they can no longer run.
    /// </summary>
    private void UpdateReadiness(
        IReadOnlyDictionary<ExecutionTaskId, ExecutionPlanTask> itemsById,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons)
    {
        foreach (ExecutionPlanTask item in itemsById.Values.Where(task => task.ExecuteAsync != null))
        {
            ExecutionTaskStatus currentStatus = statuses[item.Id];
            if (currentStatus is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Running or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Skipped or ExecutionTaskStatus.Disabled)
            {
                continue;
            }

            bool waitingForDependencies = GetTaskDependencies(item.Id, itemsById).Any(dependencyId => !IsDependencySatisfied(dependencyId, itemsById, statuses, new HashSet<ExecutionTaskId>()));
            SetStatus(statuses, statusReasons, item.Id, ExecutionTaskStatus.Pending, waitingForDependencies ? WaitingForDependenciesReason : null);
        }
    }

    /// <summary>
    /// Returns whether one dependency is satisfied strongly enough for downstream work to proceed.
    /// </summary>
    private static bool IsDependencySatisfied(
        ExecutionTaskId dependencyId,
        IReadOnlyDictionary<ExecutionTaskId, ExecutionPlanTask> itemsById,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        ISet<ExecutionTaskId> visited)
    {
        if (!visited.Add(dependencyId))
        {
            return false;
        }

        ExecutionTaskStatus status = statuses[dependencyId];
        if (status == ExecutionTaskStatus.Completed)
        {
            return true;
        }

        if (status != ExecutionTaskStatus.Disabled)
        {
            return false;
        }

        return GetTaskDependencies(dependencyId, itemsById).All(parentDependencyId => IsDependencySatisfied(parentDependencyId, itemsById, statuses, visited));
    }

    /// <summary>
    /// Marks the transitive dependents of a failed task as skipped because they can no longer run.
    /// </summary>
    private void SkipDependentsOfFailure(
        ExecutionTaskId failedTaskId,
        IReadOnlyDictionary<ExecutionTaskId, ExecutionPlanTask> itemsById,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons)
    {
        foreach (ExecutionPlanTask dependent in itemsById.Values.Where(item => item.ExecuteAsync != null && GetTaskDependencies(item.Id, itemsById).Contains(failedTaskId)))
        {
            if (statuses[dependent.Id] is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Skipped or ExecutionTaskStatus.Disabled)
            {
                continue;
            }

            SetStatus(statuses, statusReasons, dependent.Id, ExecutionTaskStatus.Skipped, $"Skipped because dependency '{failedTaskId}' failed.");
            SkipDependentsOfFailure(dependent.Id, itemsById, statuses, statusReasons);
        }
    }

    /// <summary>
    /// Marks every non-terminal task after a global cancellation request, preserving Cancelled only for work that was
    /// actively running and using Skipped for work that never started.
    /// </summary>
    private void CancelOutstandingTasks(
        IReadOnlyDictionary<ExecutionTaskId, ExecutionPlanTask> itemsById,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons)
    {
        foreach (ExecutionPlanTask item in itemsById.Values.Where(task => task.ExecuteAsync != null))
        {
            if (statuses[item.Id] is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Skipped or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Disabled)
            {
                continue;
            }

            ExecutionTaskStatus nextStatus = statuses[item.Id] == ExecutionTaskStatus.Running
                ? ExecutionTaskStatus.Cancelled
                : ExecutionTaskStatus.Skipped;
            string reason = nextStatus == ExecutionTaskStatus.Cancelled
                ? "Cancelled."
                : "Skipped because execution was cancelled.";
            SetStatus(statuses, statusReasons, item.Id, nextStatus, reason);
        }
    }

    /// <summary>
    /// Returns the direct dependency ids for one executable task, excluding purely hierarchical parent relationships.
    /// </summary>
    private static IReadOnlyList<ExecutionTaskId> GetTaskDependencies(ExecutionTaskId taskId, IReadOnlyDictionary<ExecutionTaskId, ExecutionPlanTask> itemsById)
    {
        if (!itemsById.TryGetValue(taskId, out ExecutionPlanTask? task))
        {
            return Array.Empty<ExecutionTaskId>();
        }

        return task.DependsOn;
    }

    /// <summary>
    /// Creates the logger that should receive output for one executing task.
    /// </summary>
    private ILogger CreateTaskLogger(ExecutionTaskId taskId)
    {
        if (_logger is IExecutionTaskLoggerFactory loggerFactory)
        {
            return loggerFactory.CreateTaskLogger(taskId);
        }

        return _logger;
    }

    /// <summary>
    /// Executes one authored task callback so task-body time is measured separately from scheduler bookkeeping.
    /// </summary>
    private async Task<OperationResult> ExecuteTaskBodyAsync(ExecutionPlanTask task, ExecutionTaskContext context)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.ExecuteTaskBody")
            .SetTag("task.id", task.Id.Value)
            .SetTag("task.title", task.Title);
        try
        {
            OperationResult result = await task.ExecuteAsync!(context).ConfigureAwait(false);
            activity.SetTag("task.outcome", result.Outcome.ToString());
            return result;
        }
        catch (OperationCanceledException)
        {
            activity.SetTag("task.outcome", RunOutcome.Cancelled.ToString());
            _taskStateSink?.SetTaskStatus(context.TaskId, ExecutionTaskStatus.Cancelled, "Cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            activity.SetTag("task.outcome", "Exception")
                .SetTag("exception.type", ex.GetType().FullName ?? ex.GetType().Name);
            _taskStateSink?.SetTaskStatus(context.TaskId, ExecutionTaskStatus.Failed, ex.Message);
            throw new Exception($"Exception encountered running execution task '{task.Title}'", ex);
        }
    }

    /// <summary>
    /// Records a task-status transition in the in-memory status map and forwards it to the active session sink.
    /// </summary>
    private void SetStatus(
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons,
        ExecutionTaskId taskId,
        ExecutionTaskStatus status,
        string? reason = null)
    {
        statuses[taskId] = status;
        statusReasons[taskId] = reason;
        _taskStateSink?.SetTaskStatus(taskId, status, reason);
        _logger.LogDebug("Execution task '{TaskId}' -> {Status}. {Reason}", taskId, status, reason ?? string.Empty);
    }

    /// <summary>
    /// Compares task instances by reference so the scheduler can map completed tasks back to their task ids.
    /// </summary>
    private sealed class TaskComparer : IEqualityComparer<Task<OperationResult>>
    {
        /// <summary>
        /// Gets the singleton task comparer instance.
        /// </summary>
        public static TaskComparer Instance { get; } = new();

        /// <summary>
        /// Compares two task references.
        /// </summary>
        public bool Equals(Task<OperationResult>? x, Task<OperationResult>? y)
        {
            return ReferenceEquals(x, y);
        }

        /// <summary>
        /// Returns the reference-based hash code for the task.
        /// </summary>
        public int GetHashCode(Task<OperationResult> obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
