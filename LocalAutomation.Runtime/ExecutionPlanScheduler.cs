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
    /// Executes the provided work items until they complete, fail, are skipped because they can no longer run, or are
    /// cancelled.
    /// </summary>
    public async Task<OperationResult> ExecuteAsync(IEnumerable<ExecutionWorkItem> workItems, CancellationToken cancellationToken)
    {
        Dictionary<ExecutionTaskId, ExecutionWorkItem> itemsById = Materialize(workItems);
        Dictionary<ExecutionTaskId, ExecutionTaskStatus> statuses = new();
        Dictionary<ExecutionTaskId, string?> statusReasons = new();
        Dictionary<ExecutionTaskId, Task<OperationResult>> runningTasks = new();
        Dictionary<Task<OperationResult>, ExecutionTaskId> taskOwners = new(TaskComparer.Instance);
        bool encounteredFailure = false;
        bool encounteredCancellation = false;

        foreach (ExecutionWorkItem item in itemsById.Values)
        {
            if (!item.Enabled)
            {
                SetStatus(statuses, statusReasons, item.TaskId, ExecutionTaskStatus.Skipped, item.SkippedReason);
                continue;
            }

            // Before a task actually starts, the design only needs to know that the task is still pending. Whether it
            // is waiting on dependencies or scheduler turn is internal scheduler detail rather than a user-facing state.
            SetStatus(statuses, statusReasons, item.TaskId, ExecutionTaskStatus.Pending, item.DependsOn.Count > 0 ? WaitingForDependenciesReason : null);
        }

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                encounteredCancellation = true;
                CancelOutstandingTasks(itemsById, statuses, statusReasons);
                break;
            }

            bool startedAny = StartReadyItems(itemsById, statuses, statusReasons, runningTasks, taskOwners, cancellationToken);
            if (runningTasks.Count == 0)
            {
                IReadOnlyList<ExecutionTaskId> incompleteTasks = statuses
                    .Where(pair => pair.Value is not ExecutionTaskStatus.Completed and not ExecutionTaskStatus.Cancelled and not ExecutionTaskStatus.Skipped)
                    .Select(pair => pair.Key)
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
    /// Materializes and validates the declared work-item set before execution begins.
    /// </summary>
    private static Dictionary<ExecutionTaskId, ExecutionWorkItem> Materialize(IEnumerable<ExecutionWorkItem> workItems)
    {
        if (workItems == null)
        {
            throw new ArgumentNullException(nameof(workItems));
        }

        Dictionary<ExecutionTaskId, ExecutionWorkItem> itemsById = new();
        foreach (ExecutionWorkItem item in workItems)
        {
            if (!itemsById.TryAdd(item.TaskId, item))
            {
                throw new InvalidOperationException($"Execution scheduler contains duplicate work item '{item.TaskId}'.");
            }
        }

        foreach (ExecutionWorkItem item in itemsById.Values)
        {
            foreach (ExecutionTaskId dependencyId in item.DependsOn)
            {
                if (!itemsById.ContainsKey(dependencyId))
                {
                    throw new InvalidOperationException($"Execution work item '{item.TaskId}' depends on unknown item '{dependencyId}'.");
                }
            }
        }

        return itemsById;
    }

    /// <summary>
    /// Starts any ready work items while scheduler capacity remains available.
    /// </summary>
    private bool StartReadyItems(
        IReadOnlyDictionary<ExecutionTaskId, ExecutionWorkItem> itemsById,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons,
        IDictionary<ExecutionTaskId, Task<OperationResult>> runningTasks,
        IDictionary<Task<OperationResult>, ExecutionTaskId> taskOwners,
        CancellationToken cancellationToken)
    {
        bool startedAny = false;
        foreach (ExecutionWorkItem item in itemsById.Values.Where(item => statuses[item.TaskId] == ExecutionTaskStatus.Pending && item.DependsOn.All(dependencyId => statuses[dependencyId] == ExecutionTaskStatus.Completed)).OrderBy(item => item.TaskId.Value, StringComparer.Ordinal))
        {
            if (runningTasks.Count >= _maxParallelism)
            {
                break;
            }

            SetStatus(statuses, statusReasons, item.TaskId, ExecutionTaskStatus.Running);
            ExecutionTaskContext context = new(item.TaskId, item.Title, CreateTaskLogger(item.TaskId), cancellationToken);
            Task<OperationResult> task = item.ExecuteAsync(context);
            runningTasks[item.TaskId] = task;
            taskOwners[task] = item.TaskId;
            startedAny = true;
        }

        return startedAny;
    }

    /// <summary>
    /// Recomputes which pending items remain eligible to run. Tasks stay pending until they either start running or are
    /// marked terminal because they can no longer run.
    /// </summary>
    private void UpdateReadiness(
        IReadOnlyDictionary<ExecutionTaskId, ExecutionWorkItem> itemsById,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons)
    {
        foreach (ExecutionWorkItem item in itemsById.Values)
        {
            ExecutionTaskStatus currentStatus = statuses[item.TaskId];
            if (currentStatus is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Running or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Skipped)
            {
                continue;
            }

            bool waitingForDependencies = item.DependsOn.Any(dependencyId => statuses[dependencyId] != ExecutionTaskStatus.Completed);
            SetStatus(statuses, statusReasons, item.TaskId, ExecutionTaskStatus.Pending, waitingForDependencies ? WaitingForDependenciesReason : null);
        }
    }

    /// <summary>
    /// Marks the transitive dependents of a failed task as skipped because they can no longer run.
    /// </summary>
    private void SkipDependentsOfFailure(
        ExecutionTaskId failedTaskId,
        IReadOnlyDictionary<ExecutionTaskId, ExecutionWorkItem> itemsById,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons)
    {
        foreach (ExecutionWorkItem dependent in itemsById.Values.Where(item => item.DependsOn.Contains(failedTaskId)))
        {
            if (statuses[dependent.TaskId] is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled)
            {
                continue;
            }

            SetStatus(statuses, statusReasons, dependent.TaskId, ExecutionTaskStatus.Skipped, $"Skipped because dependency '{failedTaskId}' failed.");
            SkipDependentsOfFailure(dependent.TaskId, itemsById, statuses, statusReasons);
        }
    }

    /// <summary>
    /// Marks every non-terminal task after a global cancellation request, preserving Cancelled only for work that was
    /// actively running and using Skipped for work that never started.
    /// </summary>
    private void CancelOutstandingTasks(
        IReadOnlyDictionary<ExecutionTaskId, ExecutionWorkItem> itemsById,
        IDictionary<ExecutionTaskId, ExecutionTaskStatus> statuses,
        IDictionary<ExecutionTaskId, string?> statusReasons)
    {
        foreach (ExecutionWorkItem item in itemsById.Values)
        {
            if (statuses[item.TaskId] is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Skipped or ExecutionTaskStatus.Cancelled)
            {
                continue;
            }

            ExecutionTaskStatus nextStatus = statuses[item.TaskId] == ExecutionTaskStatus.Running
                ? ExecutionTaskStatus.Cancelled
                : ExecutionTaskStatus.Skipped;
            string reason = nextStatus == ExecutionTaskStatus.Cancelled
                ? "Cancelled."
                : "Skipped because execution was cancelled.";
            SetStatus(statuses, statusReasons, item.TaskId, nextStatus, reason);
        }
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
