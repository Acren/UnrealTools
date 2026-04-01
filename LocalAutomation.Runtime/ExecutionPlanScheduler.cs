using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Executes one live execution session graph. The initial plan only seeds the session; after execution begins the
/// scheduler always reads tasks, dependencies, and task states from the current session so runtime graph growth remains
/// part of the same run instead of spawning detached child schedulers.
/// </summary>
public sealed class ExecutionPlanScheduler
{
    private const string WaitingForDependenciesReason = "Waiting for dependencies.";
    private const string UnsatisfiedDependenciesReason = "Scheduler could not satisfy the remaining dependencies.";

    private readonly ILogger _logger;
    private readonly IExecutionTaskStateSink? _taskStateSink;
    private readonly int _maxParallelism;
    private readonly ExecutionSession _session;
    private readonly object _syncRoot = new();
    private readonly Dictionary<ExecutionTaskId, Task<OperationResult>> _runningTasks = new();
    private readonly Dictionary<Task<OperationResult>, ExecutionTaskId> _taskOwners = new(TaskComparer.Instance);
    private readonly Dictionary<ExecutionTaskId, ChildPumpRegistration> _childPumpRegistrationsByParent = new();
    private IDictionary<Type, object> _sharedData = null!;
    private bool _encounteredFailure;
    private bool _encounteredCancellation;
    private DateTime _lastCompletionHandledAtUtc = DateTime.MinValue;
    private ExecutionTaskId? _lastCompletedTaskId;

    /// <summary>
    /// Creates a scheduler that routes task-state updates through the provided logger/session pair.
    /// </summary>
    public ExecutionPlanScheduler(ILogger logger, int? maxParallelism = null, ExecutionSession? session = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _taskStateSink = logger as IExecutionTaskStateSink;
        _maxParallelism = Math.Max(1, maxParallelism ?? Environment.ProcessorCount);
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Executes the current live session until all runnable work reaches a terminal state.
    /// </summary>
    public async Task<OperationResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken, IDictionary<Type, object>? sharedData = null)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        _sharedData = sharedData ?? new Dictionary<Type, object>();
        InitializeSessionStates();

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _encounteredCancellation = true;
                CancelOutstandingTasks();
                break;
            }

            bool startedAny = StartReadyItems(cancellationToken);
            IReadOnlyList<ExecutionTaskId> incompleteTaskIds = GetIncompleteExecutableTaskIds();
            if (incompleteTaskIds.Count == 0)
            {
                break;
            }

            Task<OperationResult>[] runningTasksSnapshot = GetActiveRunningTasksSnapshot();

            if (runningTasksSnapshot.Length == 0)
            {
                if (!startedAny)
                {
                    MarkUnsatisfiedTasks(incompleteTaskIds);
                    _encounteredFailure = true;
                    break;
                }

                continue;
            }

            Task<OperationResult> finishedTask = await Task.WhenAny(runningTasksSnapshot).ConfigureAwait(false);
            await HandleCompletedTaskAsync(finishedTask).ConfigureAwait(false);
        }

        if (_encounteredFailure || _session.Tasks.Any(task => task.Status == ExecutionTaskStatus.Failed))
        {
            return OperationResult.Failed();
        }

        if (_encounteredCancellation || _session.Tasks.Any(task => task.Status == ExecutionTaskStatus.Cancelled))
        {
            return OperationResult.Cancelled();
        }

        return OperationResult.Succeeded();
    }

    /// <summary>
    /// Waits for one set of newly inserted child tasks while temporarily yielding the parent's scheduler slot so the
    /// same root scheduler can keep pumping the live session graph.
    /// </summary>
    internal async Task<OperationResult> WaitForInsertedChildTasksAsync(Operation operation, ExecutionTaskContext parentContext, ChildTaskMergeResult mergeResult)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (parentContext == null)
        {
            throw new ArgumentNullException(nameof(parentContext));
        }

        if (mergeResult == null)
        {
            throw new ArgumentNullException(nameof(mergeResult));
        }

        if (mergeResult.InsertedTaskIds.Count == 0)
        {
            return OperationResult.Succeeded();
        }

        RegisterChildPump(parentContext.TaskId, mergeResult.InsertedTaskIds);
        try
        {
            await _session.WaitForTaskCompletionAsync(mergeResult.InsertedTaskIds, parentContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CompleteChildPump(parentContext.TaskId);
        }

        return BuildChildOperationResult(operation, mergeResult.InsertedTaskIds);
    }

    /// <summary>
    /// Ensures every current task in the live session starts in a runtime-ready state before scheduling begins.
    /// </summary>
    private void InitializeSessionStates()
    {
        foreach (ExecutionTask task in _session.Tasks)
        {
            if (task.ExecuteAsync == null)
            {
                continue;
            }

            if (!task.Enabled)
            {
                SetStatus(task.Id, ExecutionTaskStatus.Disabled, task.DisabledReason);
                continue;
            }

            if (task.Status == ExecutionTaskStatus.Planned)
            {
                SetStatus(task.Id, ExecutionTaskStatus.Pending, task.DependsOn.Count > 0 ? WaitingForDependenciesReason : null);
            }
        }
    }

    /// <summary>
    /// Starts every ready session task while capacity remains available and the parent is not currently yielding its slot
    /// to newly inserted child tasks.
    /// </summary>
    private bool StartReadyItems(CancellationToken cancellationToken)
    {
        bool startedAny = false;
        List<ExecutionTask> readyTasks = _session.Tasks
            .Where(task => task.ExecuteAsync != null)
            .Where(task => !IsTaskPausedForChildPumping(task.Id))
            .Where(task => task.Status == ExecutionTaskStatus.Pending)
            .Where(task => task.DependsOn.All(IsDependencySatisfied))
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .ToList();
        using PerformanceActivityScope readyActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.StartReadyItems")
            .SetTag("ready.count", readyTasks.Count)
            .SetTag("running.count", GetActiveRunningTaskCount())
            .SetTag("pending.count", _session.Tasks.Count(task => task.Status == ExecutionTaskStatus.Pending));
        if (_lastCompletedTaskId != null)
        {
            readyActivity.SetTag("completed.task.id", _lastCompletedTaskId.Value.Value)
                .SetTag("completion_to_ready_ms", _lastCompletionHandledAtUtc == DateTime.MinValue ? "0" : (DateTime.UtcNow - _lastCompletionHandledAtUtc).TotalMilliseconds.ToString("0"));
        }

        int startedCount = 0;
        foreach (ExecutionTask task in readyTasks)
        {
            lock (_syncRoot)
            {
                if (GetActiveRunningTaskCountUnsafe() >= _maxParallelism || _runningTasks.ContainsKey(task.Id))
                {
                    break;
                }
            }

            using PerformanceActivityScope startActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.StartTask")
                .SetTag("task.id", task.Id.Value)
                .SetTag("task.title", task.Title);
            if (_lastCompletedTaskId != null)
            {
                startActivity.SetTag("previous.task.id", _lastCompletedTaskId.Value.Value)
                    .SetTag("completion_to_start_ms", _lastCompletionHandledAtUtc == DateTime.MinValue ? "0" : (DateTime.UtcNow - _lastCompletionHandledAtUtc).TotalMilliseconds.ToString("0"));
            }

            SetStatus(task.Id, ExecutionTaskStatus.Running);
            ExecutionTaskContext context = new(task.Id, task.Title, CreateTaskLogger(task.Id), cancellationToken, task.OperationParameters, _sharedData, _session, this);
            Task<OperationResult> runningTask = ExecuteTaskBodyAsync(task, context);
            lock (_syncRoot)
            {
                _runningTasks[task.Id] = runningTask;
                _taskOwners[runningTask] = task.Id;
            }

            startedAny = true;
            startedCount += 1;
        }

        readyActivity.SetTag("started.count", startedCount);
        return startedAny;
    }

    /// <summary>
    /// Processes one completed task and updates live session state based on the task outcome.
    /// </summary>
    private async Task HandleCompletedTaskAsync(Task<OperationResult> completedTask)
    {
        ExecutionTaskId completedTaskId;
        lock (_syncRoot)
        {
            completedTaskId = _taskOwners[completedTask];
            _taskOwners.Remove(completedTask);
            _runningTasks.Remove(completedTaskId);
        }

        using PerformanceActivityScope completionActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.HandleCompletedTask")
            .SetTag("task.id", completedTaskId.Value);

        try
        {
            OperationResult result = await completedTask.ConfigureAwait(false);
            completionActivity.SetTag("task.outcome", result.Outcome.ToString());

            if (result.Outcome == RunOutcome.Succeeded)
            {
                if (!IsTaskPausedForChildPumping(completedTaskId))
                {
                    SetStatus(completedTaskId, ExecutionTaskStatus.Completed);
                }
            }
            else if (result.Outcome == RunOutcome.Cancelled)
            {
                _encounteredCancellation = true;
                SetStatus(completedTaskId, ExecutionTaskStatus.Cancelled, "Cancelled.");
                CancelOutstandingTasks();
            }
            else
            {
                _encounteredFailure = true;
                _session.FailScopeFromTask(completedTaskId, "The task execution callback returned failure.");
            }
        }
        catch (OperationCanceledException)
        {
            _encounteredCancellation = true;
            SetStatus(completedTaskId, ExecutionTaskStatus.Cancelled, "Cancelled.");
            CancelOutstandingTasks();
        }
        catch (Exception ex)
        {
            _encounteredFailure = true;
            _session.FailScopeFromTask(completedTaskId, ex.Message);
        }

        UpdateReadiness();
        _lastCompletedTaskId = completedTaskId;
        _lastCompletionHandledAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Recomputes pending-state reasons after new tasks are added or tasks finish.
    /// </summary>
    private void UpdateReadiness()
    {
        foreach (ExecutionTask task in _session.Tasks.Where(task => task.ExecuteAsync != null))
        {
            ExecutionTaskStatus currentStatus = task.Status;
            if (currentStatus is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Running or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Skipped or ExecutionTaskStatus.Disabled)
            {
                continue;
            }

            bool waitingForDependencies = task.DependsOn.Any(dependencyId => !IsDependencySatisfied(dependencyId));
            SetStatus(task.Id, ExecutionTaskStatus.Pending, waitingForDependencies ? WaitingForDependenciesReason : null);
        }
    }

    /// <summary>
    /// Returns every executable task that has not yet reached a terminal state.
    /// </summary>
    private IReadOnlyList<ExecutionTaskId> GetIncompleteExecutableTaskIds()
    {
        return _session.Tasks
            .Where(task => task.ExecuteAsync != null)
            .Select(task => task.Id)
            .Where(taskId => !_session.IsTaskTerminal(taskId) && !IsTaskPausedForChildPumping(taskId))
            .ToList();
    }

    /// <summary>
    /// Marks every remaining pending task as unsatisfied when no further work can be started.
    /// </summary>
    private void MarkUnsatisfiedTasks(IEnumerable<ExecutionTaskId> taskIds)
    {
        foreach (ExecutionTaskId taskId in taskIds)
        {
            ExecutionTaskStatus currentStatus = _session.GetTask(taskId).Status;
            if (currentStatus is ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled)
            {
                continue;
            }

            SetStatus(taskId, ExecutionTaskStatus.Skipped, UnsatisfiedDependenciesReason);
        }
    }

    /// <summary>
    /// Returns whether a dependency is satisfied strongly enough for downstream work to start.
    /// </summary>
    private bool IsDependencySatisfied(ExecutionTaskId dependencyId)
    {
        ExecutionTask dependencyTask = _session.GetTask(dependencyId);
        ExecutionTaskStatus status = dependencyTask.Status;
        if (status == ExecutionTaskStatus.Completed)
        {
            return true;
        }

        if (status != ExecutionTaskStatus.Disabled)
        {
            return false;
        }

        return dependencyTask.DependsOn.All(IsDependencySatisfied);
    }

    /// <summary>
    /// Marks all remaining runnable tasks after a global cancellation request.
    /// </summary>
    private void CancelOutstandingTasks()
    {
        foreach (ExecutionTask task in _session.Tasks.Where(task => task.ExecuteAsync != null))
        {
            ExecutionTaskStatus currentStatus = task.Status;
            if (currentStatus is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Skipped or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Disabled)
            {
                continue;
            }

            ExecutionTaskStatus nextStatus = currentStatus == ExecutionTaskStatus.Running
                ? ExecutionTaskStatus.Cancelled
                : ExecutionTaskStatus.Skipped;
            string reason = nextStatus == ExecutionTaskStatus.Cancelled
                ? "Cancelled."
                : "Skipped because execution was cancelled.";
            SetStatus(task.Id, nextStatus, reason);
        }
    }

    /// <summary>
    /// Registers that the current parent task is waiting on newly inserted child tasks so its scheduler slot can be
    /// yielded temporarily while the same scheduler continues pumping the live graph.
    /// </summary>
    private void RegisterChildPump(ExecutionTaskId parentTaskId, IReadOnlyCollection<ExecutionTaskId> childTaskIds)
    {
        ChildPumpRegistration registration = new(parentTaskId, childTaskIds);
        lock (_syncRoot)
        {
            _childPumpRegistrationsByParent[parentTaskId] = registration;
        }

        UpdateReadiness();
    }

    /// <summary>
    /// Restores the parent task to the running set after its inserted child work finishes so outer scheduler bookkeeping
    /// can complete the parent task normally when its callback returns.
    /// </summary>
    private void CompleteChildPump(ExecutionTaskId parentTaskId)
    {
        lock (_syncRoot)
        {
            _childPumpRegistrationsByParent.Remove(parentTaskId);
        }
    }

    /// <summary>
    /// Returns whether one task is currently yielding its slot while it awaits nested child work.
    /// </summary>
    private bool IsTaskPausedForChildPumping(ExecutionTaskId taskId)
    {
        lock (_syncRoot)
        {
            return _childPumpRegistrationsByParent.ContainsKey(taskId);
        }
    }

    /// <summary>
    /// Returns the currently running tasks that still consume scheduler capacity. Parent tasks paused while awaiting
    /// nested child work remain tracked in the running map so their eventual completion is still observed, but they do
    /// not count as active capacity or completion sources while the child work is being pumped.
    /// </summary>
    private Task<OperationResult>[] GetActiveRunningTasksSnapshot()
    {
        lock (_syncRoot)
        {
            return _runningTasks
                .Where(pair => !_childPumpRegistrationsByParent.ContainsKey(pair.Key))
                .Select(pair => pair.Value)
                .ToArray();
        }
    }

    /// <summary>
    /// Returns the number of capacity-consuming running tasks.
    /// </summary>
    private int GetActiveRunningTaskCount()
    {
        lock (_syncRoot)
        {
            return GetActiveRunningTaskCountUnsafe();
        }
    }

    /// <summary>
    /// Counts capacity-consuming running tasks while the caller already holds the scheduler lock.
    /// </summary>
    private int GetActiveRunningTaskCountUnsafe()
    {
        return _runningTasks.Keys.Count(taskId => !_childPumpRegistrationsByParent.ContainsKey(taskId));
    }

    /// <summary>
    /// Builds the child-operation result from the terminal states reached by the inserted child tasks.
    /// </summary>
    private OperationResult BuildChildOperationResult(Operation operation, IReadOnlyCollection<ExecutionTaskId> childTaskIds)
    {
        List<string> warnings = new();
        List<string> errors = new();
        foreach (LogEntry entry in _session.LogStream.Entries.Where(entry => ExecutionTaskId.FromNullable(entry.TaskId) is ExecutionTaskId taskId && childTaskIds.Contains(taskId)))
        {
            if (entry.Verbosity >= LogLevel.Error)
            {
                errors.Add(entry.Message);
            }
            else if (entry.Verbosity == LogLevel.Warning)
            {
                warnings.Add(entry.Message);
            }
        }

        IReadOnlyList<ExecutionTask> childStates = childTaskIds
            .Select(taskId => _session.GetTask(taskId))
            .ToList();
        if (childStates.Any(state => state.Status == ExecutionTaskStatus.Cancelled))
        {
            return OperationResult.Cancelled();
        }

        if (childStates.Any(state => state.Status == ExecutionTaskStatus.Failed || state.Status == ExecutionTaskStatus.Skipped))
        {
            return OperationResult.Failed();
        }

        if (warnings.Count > 0 && operation.ShouldFailOnWarning())
        {
            return OperationResult.Failed();
        }

        return OperationResult.Succeeded();
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
    /// Executes one task body while preserving task-state error and cancellation reporting at the scheduler boundary.
    /// </summary>
    private async Task<OperationResult> ExecuteTaskBodyAsync(ExecutionTask task, ExecutionTaskContext context)
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
    /// Records one task-status transition in the session and mirrors it through the active task-state sink.
    /// </summary>
    private void SetStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? reason = null)
    {
        _session.SetTaskStatus(taskId, status, reason);
        _taskStateSink?.SetTaskStatus(taskId, status, reason);
        _logger.LogDebug("Execution task '{TaskId}' -> {Status}. {Reason}", taskId, status, reason ?? string.Empty);
    }

    /// <summary>
    /// Carries the current parent task id and inserted child ids while one task is temporarily yielding its scheduler slot.
    /// </summary>
    private sealed class ChildPumpRegistration
    {
        public ChildPumpRegistration(ExecutionTaskId parentTaskId, IReadOnlyCollection<ExecutionTaskId> childTaskIds)
        {
            ParentTaskId = parentTaskId;
            ChildTaskIds = childTaskIds ?? throw new ArgumentNullException(nameof(childTaskIds));
        }

        public ExecutionTaskId ParentTaskId { get; }

        public IReadOnlyCollection<ExecutionTaskId> ChildTaskIds { get; }
    }

    /// <summary>
    /// Compares task objects by reference so completed worker tasks can be mapped back to task ids.
    /// </summary>
    private sealed class TaskComparer : IEqualityComparer<Task<OperationResult>>
    {
        public static TaskComparer Instance { get; } = new();

        public bool Equals(Task<OperationResult>? x, Task<OperationResult>? y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(Task<OperationResult> obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
