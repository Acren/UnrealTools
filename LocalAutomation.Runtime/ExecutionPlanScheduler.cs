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
    private readonly ExecutionSession _session;
    private readonly object _syncRoot = new();
    private readonly object _workSignalSyncRoot = new();
    private readonly Dictionary<ExecutionTaskId, Task<OperationResult>> _runningTasks = new();
    private readonly Dictionary<Task<OperationResult>, ExecutionTaskId> _taskOwners = new(TaskComparer.Instance);
    private IDictionary<Type, object> _sharedData = null!;
    private TaskCompletionSource<bool> _workAvailableSignal = CreateWorkAvailableSignal();
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
        _session.TaskGraphChanged += HandleSessionWorkChanged;
        _session.TaskStatusChanged += HandleSessionTaskStatusChanged;
        InitializeSessionStates();
        try
        {
            while (true)
            {
                ResetWorkAvailableSignalIfCompleted();

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

                Task workAvailableTask = GetWorkAvailableSignalTask();
                Task completedTrigger = await Task.WhenAny(runningTasksSnapshot.Cast<Task>().Append(workAvailableTask)).ConfigureAwait(false);
                if (ReferenceEquals(completedTrigger, workAvailableTask))
                {
                    continue;
                }

                await HandleCompletedTaskAsync((Task<OperationResult>)completedTrigger).ConfigureAwait(false);
            }
        }
        finally
        {
            _session.TaskGraphChanged -= HandleSessionWorkChanged;
            _session.TaskStatusChanged -= HandleSessionTaskStatusChanged;
        }

        /* Run outcome is synthesized from semantic results. Lifecycle state alone only answers whether work is still
           active, so failure/cancellation detection must read Result instead of Status. */
        if (_encounteredFailure || _session.Tasks.Any(task => task.Result == ExecutionTaskStatus.Failed))
        {
            string? failureReason = _session.Tasks
                .Select(task => task.StatusReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            return OperationResult.Failed(failureReason: failureReason);
        }

        if (_encounteredCancellation || _session.Tasks.Any(task => task.Result == ExecutionTaskStatus.Cancelled))
        {
            return OperationResult.Cancelled();
        }

        return OperationResult.Succeeded();
    }

    /// <summary>
    /// Waits for one set of newly inserted child tasks. The scheduler itself stays purely dependency-driven; the wait is
    /// satisfied by ordinary task completion in the live session graph rather than by a special child-pump scheduler mode.
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

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.WaitForInsertedChildTasks")
            .SetTag("parent.task.id", parentContext.TaskId.Value)
            .SetTag("child.operation", operation.OperationName)
            .SetTag("inserted.task.count", mergeResult.InsertedTaskIds.Count)
            .SetTag("inserted.root.id", mergeResult.RootTask.Id.Value)
            .SetTag("inserted.root.title", mergeResult.RootTask.Title);

        if (mergeResult.InsertedTaskIds.Count == 0)
        {
            return OperationResult.Succeeded();
        }

        using (PerformanceActivityScope waitActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.WaitForInsertedChildTasks.Wait"))
        {
            waitActivity.SetTag("parent.task.id", parentContext.TaskId.Value)
                .SetTag("inserted.root.id", mergeResult.RootTask.Id.Value)
                .SetTag("inserted.task.count", mergeResult.InsertedTaskIds.Count);
            await _session.WaitForTaskCompletionAsync(mergeResult.InsertedTaskIds, parentContext.CancellationToken).ConfigureAwait(false);
        }

        OperationResult result = BuildChildOperationResult(operation, mergeResult.InsertedTaskIds);
        activity.SetTag("result.outcome", result.Outcome.ToString())
            .SetTag("result.success", result.Success);
        return result;
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
                _session.CompleteTaskWithResult(task.Id, ExecutionTaskStatus.Disabled, task.DisabledReason);
                continue;
            }

            if (task.Status == ExecutionTaskStatus.Planned)
            {
                SetStatus(task.Id, ExecutionTaskStatus.Pending, task.DependsOn.Count > 0 ? WaitingForDependenciesReason : null);
            }
        }
    }

    /// <summary>
    /// Signals the scheduler whenever the live graph changes in a way that may have created newly startable work.
    /// </summary>
    private void HandleSessionWorkChanged()
    {
        SignalWorkAvailable();
    }

    /// <summary>
    /// Signals the scheduler when task-state changes may unblock downstream work without requiring a task completion to be
    /// the only wake-up source.
    /// </summary>
    private void HandleSessionTaskStatusChanged(ExecutionTaskId _, ExecutionTaskStatus __, string? ___)
    {
        SignalWorkAvailable();
    }

    /// <summary>
    /// Starts every ready session task. Ordering is entirely dependency-driven, so the scheduler no longer needs a
    /// separate parent-pump mode or semantic worker-capacity gate.
    /// </summary>
    private bool StartReadyItems(CancellationToken cancellationToken)
    {
        bool startedAny = false;
        List<ExecutionTask> readyTasks = _session.Tasks
            .Where(task => task.ExecuteAsync != null)
            .Where(task => task.Status == ExecutionTaskStatus.Pending)
            .Where(task => task.DependsOn.All(IsDependencySatisfied))
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .ToList();
        using PerformanceActivityScope readyActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.StartReadyItems")
            .SetTag("ready.count", readyTasks.Count)
            .SetTag("running.count", _runningTasks.Count)
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
                if (_runningTasks.ContainsKey(task.Id))
                {
                    continue;
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
            ExecutionTaskContext context = new(task.Id, task.Title, CreateTaskLogger(task.Id), cancellationToken, new ValidatedOperationParameters(task.Title, task.OperationParameters, task.DeclaredOptionTypes), _sharedData, _session, this);
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
                _session.CompleteTaskWithResult(completedTaskId, ExecutionTaskStatus.Completed);
            }
            else if (result.Outcome == RunOutcome.Cancelled)
            {
                _encounteredCancellation = true;
                _session.CompleteTaskWithResult(completedTaskId, ExecutionTaskStatus.Cancelled, "Cancelled.");
                CancelOutstandingTasks();
            }
            else
            {
                _encounteredFailure = true;
                _session.FailScopeFromTask(completedTaskId, string.IsNullOrWhiteSpace(result.FailureReason)
                    ? "The task execution callback returned failure."
                    : result.FailureReason);
            }
        }
        catch (OperationCanceledException)
        {
            _encounteredCancellation = true;
            _session.CompleteTaskWithResult(completedTaskId, ExecutionTaskStatus.Cancelled, "Cancelled.");
            CancelOutstandingTasks();
        }
        catch (Exception ex)
        {
            ExecutionTask failedTask = _session.GetTask(completedTaskId);
            ILogger taskLogger = CreateTaskLogger(completedTaskId);
            Exception rootException = ex.GetBaseException();

            /* Callback exceptions need to land in the task log with the full exception payload so the UI can surface the
               actual failure instead of only the scheduler's generic failure summary. */
            completionActivity.SetTag("task.outcome", "Exception")
                .SetTag("exception.type", rootException.GetType().FullName ?? rootException.GetType().Name)
                .SetTag("exception.message", rootException.Message);
            taskLogger.LogError(ex, "Execution task '{TaskTitle}' failed.", failedTask.Title);

            _encounteredFailure = true;
            _session.FailScopeFromTask(completedTaskId, GetFailureReason(ex));
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
            if (currentStatus is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Running)
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
            .Where(taskId => !_session.IsTaskTerminal(taskId))
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
            if (currentStatus == ExecutionTaskStatus.Completed)
            {
                continue;
            }

            _session.CompleteTaskWithResult(taskId, ExecutionTaskStatus.Skipped, UnsatisfiedDependenciesReason);
        }
    }

    /// <summary>
    /// Returns whether a dependency is satisfied strongly enough for downstream work to start.
    /// </summary>
    private bool IsDependencySatisfied(ExecutionTaskId dependencyId)
    {
        ExecutionTask dependencyTask = _session.GetTask(dependencyId);
        /* Dependencies are satisfied only by successful or disabled semantic outcomes. A completed lifecycle with failed,
           cancelled, or skipped result still blocks downstream work. */
        if (dependencyTask.Result == ExecutionTaskStatus.Completed)
        {
            return true;
        }

        if (dependencyTask.Result != ExecutionTaskStatus.Disabled)
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
            if (currentStatus == ExecutionTaskStatus.Completed)
            {
                continue;
            }

            ExecutionTaskStatus nextResult = currentStatus == ExecutionTaskStatus.Running
                ? ExecutionTaskStatus.Cancelled
                : ExecutionTaskStatus.Skipped;
            string reason = nextResult == ExecutionTaskStatus.Cancelled
                ? "Cancelled."
                : "Skipped because execution was cancelled.";
            _session.CompleteTaskWithResult(task.Id, nextResult, reason);
        }
    }

    /// <summary>
    /// Returns the currently running task callbacks. Scheduler progress now follows only dependency readiness, so there is
    /// no separate subset of capacity-consuming tasks.
    /// </summary>
    private Task<OperationResult>[] GetActiveRunningTasksSnapshot()
    {
        lock (_syncRoot)
        {
            return _runningTasks.Values.ToArray();
        }
    }

    /// <summary>
    /// Returns the current scheduler wake task so the main loop can wait for new runnable work without polling.
    /// </summary>
    private Task GetWorkAvailableSignalTask()
    {
        lock (_workSignalSyncRoot)
        {
            return _workAvailableSignal.Task;
        }
    }

    /// <summary>
    /// Completes the current wake signal so the main loop can immediately reevaluate readiness after live graph changes.
    /// </summary>
    private void SignalWorkAvailable()
    {
        lock (_workSignalSyncRoot)
        {
            _workAvailableSignal.TrySetResult(true);
        }
    }

    /// <summary>
    /// Resets the shared wake signal after the scheduler has observed it so future graph/status changes can trigger the
    /// next scheduling pass without losing notifications that arrive around the wait boundary.
    /// </summary>
    private void ResetWorkAvailableSignalIfCompleted()
    {
        lock (_workSignalSyncRoot)
        {
            if (_workAvailableSignal.Task.IsCompleted)
            {
                _workAvailableSignal = CreateWorkAvailableSignal();
            }
        }
    }

    /// <summary>
    /// Creates the reusable scheduler wake signal with asynchronous continuations so task callbacks do not resume the
    /// scheduler inline while still holding graph-mutation call stacks.
    /// </summary>
    private static TaskCompletionSource<bool> CreateWorkAvailableSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        if (childStates.Any(state => state.Result == ExecutionTaskStatus.Cancelled))
        {
            string? cancellationReason = childStates
                .Where(state => state.Result == ExecutionTaskStatus.Cancelled)
                .Select(state => state.StatusReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            return OperationResult.Cancelled(failureReason: cancellationReason);
        }

        if (childStates.Any(state => state.Result is ExecutionTaskStatus.Failed or ExecutionTaskStatus.Skipped))
        {
            string? failureReason = childStates
                .Where(state => state.Result is ExecutionTaskStatus.Failed or ExecutionTaskStatus.Skipped)
                .Select(state => state.StatusReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            return OperationResult.Failed(failureReason: failureReason);
        }

        if (warnings.Count > 0 && operation.ShouldFailOnWarning())
        {
            return OperationResult.Failed(failureReason: "Operation fails on warnings");
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
            throw;
        }
        catch (Exception ex)
        {
            activity.SetTag("task.outcome", "Exception")
                .SetTag("exception.type", ex.GetType().FullName ?? ex.GetType().Name)
                .SetTag("exception.message", ex.Message);

            /* Preserve the original exception so the scheduler can log the real root cause and expose the innermost
               message in task status instead of a wrapper message. */
            throw;
        }
    }

    /// <summary>
    /// Returns the most useful failure reason for task status by preferring the root exception message when one exists.
    /// </summary>
    private static string GetFailureReason(Exception exception)
    {
        Exception rootException = exception.GetBaseException();
        return string.IsNullOrWhiteSpace(rootException.Message)
            ? exception.Message
            : rootException.Message;
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
