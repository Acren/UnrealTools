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
    private enum SchedulerStopReason
    {
        None,
        UserCancelled,
        InterruptedByTerminalOutcome
    }

    private readonly ILogger _logger;
    private readonly IExecutionTaskStateSink? _taskStateSink;
    private readonly ExecutionSession _session;
    private readonly object _workSignalSyncRoot = new();
    private readonly object _executionCancellationSyncRoot = new();
    private TaskCompletionSource<bool> _workAvailableSignal = CreateWorkAvailableSignal();
    private CancellationTokenSource? _schedulerCancellationSource;
    private CancellationToken _executionCancellationToken;
    private SchedulerStopReason _stopReason;
    private bool _encounteredFailure;
    private bool _encounteredCancellation;
    private DateTime _lastCompletionHandledAtUtc = DateTime.MinValue;
    private ExecutionTaskId? _lastCompletedTaskId;

    /// <summary>
    /// Creates a scheduler that routes task-state updates through the provided logger/session pair.
    /// </summary>
    public ExecutionPlanScheduler(ILogger logger, ExecutionSession? session = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _taskStateSink = logger as IExecutionTaskStateSink;
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <summary>
    /// Executes the current live session until all runnable work reaches a terminal state.
    /// </summary>
    public async Task<OperationResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource schedulerCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _schedulerCancellationSource = schedulerCancellationSource;
        _executionCancellationToken = schedulerCancellationSource.Token;
        ExecutionLocks.Changed += HandleExecutionLocksChanged;
        _session.TaskGraphChanged += HandleSessionWorkChanged;
        _session.TaskStateChanged += HandleSessionTaskChanged;
        try
        {
            while (true)
            {
                ResetWorkAvailableSignalIfCompleted();

                if (_executionCancellationToken.IsCancellationRequested)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        ApplyStopReason(SchedulerStopReason.UserCancelled);
                    }

                    CancelOutstandingTasks(_stopReason);
                    break;
                }

                bool handledCompletedTasks = await DrainCompletedRunningTasksAsync().ConfigureAwait(false);
                bool startedAny = StartReadyItems(_executionCancellationToken);
                handledCompletedTasks |= await DrainCompletedRunningTasksAsync().ConfigureAwait(false);
                IReadOnlyList<ExecutionTaskId> incompleteTaskIds = _session.GetIncompleteSchedulableTaskIds();
                if (incompleteTaskIds.Count == 0)
                {
                    break;
                }

                Task<OperationResult>[] runningTasksSnapshot = GetActiveExecutionTasksSnapshot();

                if (runningTasksSnapshot.Length == 0)
                {
                    if (HasTasksWaitingForExecutionLocks())
                    {
                        Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, _executionCancellationToken);
                        await Task.WhenAny(GetWorkAvailableSignalTask(), cancellationTask).ConfigureAwait(false);
                        continue;
                    }

                    if (!startedAny && !handledCompletedTasks)
                    {
                        /* This is the scheduler's dead-end branch. Log the exact incomplete task ids so the next run shows
                           whether work is stranded behind a non-running parent scope or missing dependency. */
                        _logger.LogWarning(
                            "Scheduler found no active running tasks and no new work to start. Marking incomplete executable tasks as unsatisfied: [{IncompleteTaskIds}]",
                            string.Join(", ", incompleteTaskIds.Select(taskId => taskId.Value)));
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
            ExecutionLocks.Changed -= HandleExecutionLocksChanged;
            _session.TaskGraphChanged -= HandleSessionWorkChanged;
            _session.TaskStateChanged -= HandleSessionTaskChanged;
            lock (_executionCancellationSyncRoot)
            {
                _schedulerCancellationSource = null;
                _executionCancellationToken = default;
            }
        }

        return BuildSessionResult();
    }

    /// <summary>
    /// Processes every running task that has already completed so visible task state is published promptly instead of
    /// waiting for one completion per outer scheduler loop iteration.
    /// </summary>
    private async Task<bool> DrainCompletedRunningTasksAsync()
    {
        bool handledAny = false;
        while (true)
        {
            Task<OperationResult>[] completedTasks = GetActiveExecutionTasksSnapshot()
                .Where(task => task.IsCompleted)
                .ToArray();
            if (completedTasks.Length == 0)
            {
                return handledAny;
            }

            foreach (Task<OperationResult> completedTask in completedTasks)
            {
                await HandleCompletedTaskAsync(completedTask).ConfigureAwait(false);
                handledAny = true;
            }
        }
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

        _logger.LogDebug(
            "Waiting for child operation '{ChildOperation}' inserted root '{InsertedRootTitle}' ({InsertedRootId}) beneath task '{ParentTaskId}'. Inserted task ids=[{InsertedTaskIds}]",
            operation.OperationName,
            mergeResult.RootTask.Title,
            mergeResult.RootTask.Id,
            parentContext.TaskId,
            string.Join(", ", mergeResult.InsertedTaskIds.Select(taskId => taskId.Value)));

        if (mergeResult.InsertedTaskIds.Count == 0)
        {
            _logger.LogWarning(
                "Child operation '{ChildOperation}' produced no inserted tasks beneath task '{ParentTaskId}'.",
                operation.OperationName,
                parentContext.TaskId);
            return OperationResult.Succeeded();
        }

        using (PerformanceActivityScope waitActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.WaitForInsertedChildTasks.Wait"))
        {
            waitActivity.SetTag("parent.task.id", parentContext.TaskId.Value)
                .SetTag("inserted.root.id", mergeResult.RootTask.Id.Value)
                .SetTag("inserted.task.count", mergeResult.InsertedTaskIds.Count);

            /* The child-operation wait boundary decides whether the parent body stays alive. Log both entry and exit so
               the next launch log can distinguish a real wait from an immediate return or cancellation. */
            List<ExecutionTaskId> initiallyNonTerminalTaskIds = mergeResult.InsertedTaskIds
                .Where(taskId => !_session.IsTaskTerminal(taskId))
                .ToList();
            _logger.LogDebug(
                "Starting inserted-task wait for child operation '{ChildOperation}'. Non-terminal task ids at wait start=[{NonTerminalTaskIds}]",
                operation.OperationName,
                string.Join(", ", initiallyNonTerminalTaskIds.Select(taskId => taskId.Value)));

            try
            {
                await _session.WaitForTaskCompletionAsync(mergeResult.InsertedTaskIds, parentContext.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                List<ExecutionTaskId> remainingTaskIds = mergeResult.InsertedTaskIds
                    .Where(taskId => !_session.IsTaskTerminal(taskId))
                    .ToList();
                _logger.LogWarning(
                    "Inserted-task wait for child operation '{ChildOperation}' was cancelled. Remaining non-terminal task ids=[{RemainingTaskIds}]",
                    operation.OperationName,
                    string.Join(", ", remainingTaskIds.Select(taskId => taskId.Value)));
                throw;
            }
        }

        List<ExecutionTaskId> nonTerminalInsertedTaskIds = mergeResult.InsertedTaskIds
            .Where(taskId => !_session.IsTaskTerminal(taskId))
            .ToList();
        if (nonTerminalInsertedTaskIds.Count > 0)
        {
            _logger.LogError(
                "Child operation '{ChildOperation}' wait returned before inserted tasks reached terminal state. Non-terminal ids=[{NonTerminalTaskIds}]",
                operation.OperationName,
                string.Join(", ", nonTerminalInsertedTaskIds.Select(taskId => taskId.Value)));
            throw new InvalidOperationException($"Child operation '{operation.OperationName}' returned before all inserted tasks reached terminal state.");
        }

        OperationResult result = BuildChildOperationResult(operation, mergeResult.InsertedTaskIds);
        activity.SetTag("result.outcome", result.Outcome.ToString())
            .SetTag("result.success", result.Success);
        _logger.LogDebug(
            "Finished inserted-task wait for child operation '{ChildOperation}'. Outcome='{Outcome}'.",
            operation.OperationName,
            result.Outcome);
        return result;
    }

    /// <summary>
    /// Signals the scheduler whenever the live graph changes in a way that may have created newly startable work.
    /// </summary>
    private void HandleSessionWorkChanged()
    {
        SignalWorkAvailable();
    }

    /// <summary>
    /// Wakes the scheduler when the global execution-lock table changes and lock waiters may be startable now.
    /// </summary>
    private void HandleExecutionLocksChanged()
    {
        SignalWorkAvailable();
    }

    /// <summary>
    /// Signals the scheduler when task-state changes may unblock downstream work without requiring a task completion to be
    /// the only wake-up source.
    /// </summary>
    private void HandleSessionTaskChanged(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.HandleSessionTaskChanged")
            .SetTag("task.id", taskId.Value)
            .SetTag("task.state", state.ToString())
            .SetTag("task.outcome", outcome?.ToString() ?? string.Empty);
        if (state == ExecutionTaskState.Completed || outcome != null)
        {
            ExecutionLocks.UnregisterWaiter(taskId);
        }

        NormalizeRunningTaskTerminalOutcome(taskId, state, outcome);

        /* Running tasks only stop promptly when their shared execution token is cancelled. Session state alone is not
           enough because process-backed work observes cancellation through the task context token, so any externally
           forced terminal state must trigger the same cancellation path that user cancellation uses. */
        bool shouldCancelExecution = ShouldCancelExecutionForTerminalOutcome(taskId, state, outcome);
        activity.SetTag("execution.cancel.requested", shouldCancelExecution);
        if (shouldCancelExecution)
        {
            ApplyStopReason(ResolveStopReason(outcome));
            RequestExecutionCancellation();

            /* Forced terminal states should also immediately close out untouched queued work so the scheduler does not
               attempt to keep scheduling siblings while already-running tasks are unwinding. */
            CancelOutstandingTasks(_stopReason, preserveRunningTerminalOutcomes: true);
        }

        SignalWorkAvailable();
    }

    /// <summary>
    /// Starts every ready session task. Ordering is entirely dependency-driven, so the scheduler no longer needs a
    /// separate parent-pump mode or semantic worker-capacity gate.
    /// </summary>
    private bool StartReadyItems(CancellationToken cancellationToken)
    {
        bool startedAny = false;
        int startedCount = 0;
        int passCount = 0;
        using PerformanceActivityScope readyActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.StartReadyItems")
            .SetTag("running.count", _session.Tasks.Count(task => task.HasActiveExecution))
            .SetTag("queued.count", _session.Tasks.Count(task => task.State == ExecutionTaskState.Queued))
            .SetTag("lock_wait.count", _session.Tasks.Count(task => task.State == ExecutionTaskState.AwaitingLock));
        if (_lastCompletedTaskId != null)
        {
            readyActivity.SetTag("completed.task.id", _lastCompletedTaskId.Value.Value)
                .SetTag("completion_to_ready_ms", _lastCompletionHandledAtUtc == DateTime.MinValue ? "0" : (DateTime.UtcNow - _lastCompletionHandledAtUtc).TotalMilliseconds.ToString("0"));
        }

        while (true)
        {
            passCount += 1;
            bool startedThisPass = false;
            IReadOnlyList<ExecutionTask> readyTasks = OrderReadyTasksByDownstreamWork(_session.GetSchedulerReadyTasks());
            if (passCount == 1)
            {
                readyActivity.SetTag("ready.count", readyTasks.Count);
            }

            if (readyTasks.Count == 0)
            {
                break;
            }

            foreach (ExecutionTask task in readyTasks)
            {
                if (task.HasActiveExecution)
                {
                    continue;
                }

                using PerformanceActivityScope startActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.StartTask")
                    .SetTag("task.id", task.Id.Value)
                    .SetTag("task.title", task.Title);
                if (_lastCompletedTaskId != null)
                {
                    startActivity.SetTag("previous.task.id", _lastCompletedTaskId.Value.Value)
                        .SetTag("completion_to_start_ms", _lastCompletionHandledAtUtc == DateTime.MinValue ? "0" : (DateTime.UtcNow - _lastCompletionHandledAtUtc).TotalMilliseconds.ToString("0"));
                }

                if (task.Outcome != null)
                {
                    throw new InvalidOperationException($"Task '{task.Id}' cannot start execution while it already has semantic outcome '{task.Outcome}'.");
                }

                ExecutionTask? nextStartTask = task.TryGetNextStartableTask();
                if (nextStartTask == null)
                {
                    continue;
                }

                IReadOnlyList<ExecutionLock> executionLocks = nextStartTask.GetDeclaredExecutionLocks();
                int priority = CountDownstreamWork(nextStartTask);
                if (!TryAcquireExecutionLocksForStart(task, nextStartTask, executionLocks, priority, out IAsyncDisposable acquiredLocks))
                {
                    continue;
                }

                try
                {
                    _session.StartTaskAsync(
                        task.Id,
                        CreateTaskLogger,
                        cancellationToken,
                        this,
                        acquiredLocks,
                        (startedTask, executeAsync) =>
                        {
                            TaskCompletionSource<OperationResult> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            startedTask.AttachActiveExecution(completionSource.Task);

                            _logger.LogDebug(
                                "Dispatching task '{TaskPath}' ({TaskId}) to worker execution.",
                                _session.GetTaskDisplayPath(startedTask.Id),
                                startedTask.Id);
                            StartTaskExecutionAsync(startedTask, executeAsync, completionSource);
                            return completionSource.Task;
                        });

                    startedAny = true;
                    startedThisPass = true;
                    startedCount += 1;
                }
                catch
                {
                    acquiredLocks.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    throw;
                }
            }

            /* A pass that found structurally ready work but could not start any of it means every candidate is blocked on
               execution locks. The scheduler must yield here and wait for running work to complete instead of spinning. */
            if (!startedThisPass)
            {
                break;
            }
        }

        readyActivity.SetTag("started.count", startedCount);
        readyActivity.SetTag("pass.count", passCount);
        return startedAny;
    }

    /// <summary>
    /// Acquires the declared locks for a ready task or records that the task is globally waiting for those locks.
    /// </summary>
    private bool TryAcquireExecutionLocksForStart(
        ExecutionTask visibleTask,
        ExecutionTask executingTask,
        IReadOnlyList<ExecutionLock> executionLocks,
        int priority,
        out IAsyncDisposable acquiredLocks)
    {
        if (executionLocks.Count == 0)
        {
            acquiredLocks = ExecutionLocks.EmptyHandle;
            return true;
        }

        if (ExecutionLocks.TryAcquireOrWait(visibleTask.Id, executionLocks, priority, out acquiredLocks))
        {
            ILogger taskLogger = CreateTaskLogger(executingTask.Id);
            string lockSummary = string.Join(", ", executionLocks.Select(executionLock => executionLock.Key));
            taskLogger.LogInformation("Acquired execution lock(s): {ExecutionLocks}", lockSummary);
            acquiredLocks = new LoggedExecutionLockHandle(acquiredLocks, taskLogger, lockSummary);
            return true;
        }

        MarkTaskWaitingForExecutionLocks(visibleTask, executingTask, executionLocks);
        return false;
    }

    /// <summary>
    /// Publishes the explicit lock-wait state for a task that is ready but cannot yet receive its global lock grant.
    /// </summary>
    private void MarkTaskWaitingForExecutionLocks(ExecutionTask visibleTask, ExecutionTask executingTask, IReadOnlyList<ExecutionLock> executionLocks)
    {
        if (visibleTask.State == ExecutionTaskState.AwaitingLock)
        {
            return;
        }

        ILogger taskLogger = CreateTaskLogger(executingTask.Id);
        string lockSummary = string.Join(", ", executionLocks.Select(executionLock => executionLock.Key));
        taskLogger.LogDebug("Waiting for execution lock(s) for task '{TaskTitle}': {ExecutionLocks}", executingTask.Title, lockSummary);
        SetState(visibleTask.Id, ExecutionTaskState.AwaitingLock);
    }

    /// <summary>
    /// Orders every ready branch by how much unfinished downstream work it can unlock after the next runnable task in that
    /// branch starts and completes. Ties keep the scheduler's original ready order for determinism.
    /// </summary>
    private IReadOnlyList<ExecutionTask> OrderReadyTasksByDownstreamWork(IReadOnlyList<ExecutionTask> readyTasks)
    {
        if (readyTasks == null)
        {
            throw new ArgumentNullException(nameof(readyTasks));
        }

        return readyTasks
            .Select((task, index) =>
            {
                ExecutionTask? nextStartTask = task.TryGetNextStartableTask();
                if (nextStartTask == null || nextStartTask.HasActiveExecution)
                {
                    return null;
                }

                return new OrderedReadyTask(
                    task,
                    index,
                    CountDownstreamWork(nextStartTask));
            })
            .Where(item => item != null)
            .GroupBy(item => item!.Task.Id)
            .Select(group => group.OrderBy(item => item!.OriginalIndex).First()!)
            .OrderByDescending(item => item.DownstreamWorkCount)
            .ThenBy(item => item.OriginalIndex)
            .Select(item => item.Task)
            .ToList();
    }

    /// <summary>
    /// Counts the unfinished tasks that are transitively downstream of the concrete next-running task and each of its
    /// ancestors. Summing the full ancestor chain keeps nested child-operation work attached to the outer runtime branch
    /// that will continue unlocking work after the current runnable task completes.
    /// </summary>
    private int CountDownstreamWork(ExecutionTask task)
    {
        HashSet<ExecutionTaskId> visitedTaskIds = new();
        ExecutionTask? currentTask = task;
        while (currentTask != null)
        {
            CollectDownstreamDependentTasks(currentTask.Id, visitedTaskIds);
            currentTask = currentTask.Parent;
        }

        return visitedTaskIds.Count;
    }

    /// <summary>
    /// Walks unfinished dependency edges outward from one task id and accumulates every transitively downstream task into
    /// the provided visited set, including parent scopes that become relevant when descendant completion unlocks their
    /// own dependents.
    /// </summary>
    private void CollectDownstreamDependentTasks(ExecutionTaskId taskId, HashSet<ExecutionTaskId> visitedTaskIds)
    {
        Queue<ExecutionTaskId> pendingTaskIds = new();
        pendingTaskIds.Enqueue(taskId);

        while (pendingTaskIds.Count > 0)
        {
            ExecutionTaskId currentTaskId = pendingTaskIds.Dequeue();
            foreach (ExecutionTask dependentTask in _session.Tasks.Where(candidate => candidate.Outcome == null && candidate.Dependencies.Contains(currentTaskId)))
            {
                EnqueueDownstreamTaskAndParentScopes(dependentTask, visitedTaskIds, pendingTaskIds);
            }
        }
    }

    /// <summary>
    /// Adds one discovered downstream task and each open parent scope above it to the pending traversal queue.
    /// </summary>
    private static void EnqueueDownstreamTaskAndParentScopes(
        ExecutionTask task,
        HashSet<ExecutionTaskId> visitedTaskIds,
        Queue<ExecutionTaskId> pendingTaskIds)
    {
        /* A descendant can complete a parent scope whose id is the actual prerequisite for later fanout work. Enqueueing
           each open parent scope lets the priority walk cross that completion boundary without storing extra scheduler
           state beside the live graph. */
        for (ExecutionTask? currentTask = task; currentTask != null && currentTask.Outcome == null; currentTask = currentTask.Parent)
        {
            if (visitedTaskIds.Add(currentTask.Id))
            {
                pendingTaskIds.Enqueue(currentTask.Id);
            }
        }
    }

    private sealed record OrderedReadyTask(
        ExecutionTask Task,
        int OriginalIndex,
        int DownstreamWorkCount);

    private sealed class LoggedExecutionLockHandle(IAsyncDisposable inner, ILogger logger, string summary) : IAsyncDisposable
    {
        /// <summary>
        /// Releases the global execution-lock grant and records the matching release event in the owning task log.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("Released execution lock(s): {ExecutionLocks}", summary);
        }
    }

    /// <summary>
    /// Processes one completed task and updates live session state based on the task outcome.
    /// </summary>
    private async Task HandleCompletedTaskAsync(Task<OperationResult> completedTask)
    {
        ExecutionTask completedExecutionTask = _session.Tasks.FirstOrDefault(task => ReferenceEquals(task.ActiveExecutionTask, completedTask))
            ?? throw new InvalidOperationException("Scheduler received completion for a task that is not attached to any live execution task.");
        ExecutionTaskId completedTaskId = completedExecutionTask.Id;
        completedExecutionTask.ClearActiveExecution(completedTask);

        using PerformanceActivityScope completionActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.HandleCompletedTask")
            .SetTag("task.id", completedTaskId.Value);

        /* Separate task-body failures from runtime state-application failures. If the completed task returned normally
           but applying its result violates a task-state invariant, that indicates a framework bug and should surface
           directly instead of being converted into a synthetic task failure on an already-completed task. */
        OperationResult result;
        try
        {
            result = await completedTask.ConfigureAwait(false);
            completionActivity.SetTag("task.outcome", result.Outcome.ToString());
        }
        catch (OperationCanceledException)
        {
            ApplyCancelledTaskCompletion(completedTaskId);
            _lastCompletedTaskId = completedTaskId;
            _lastCompletionHandledAtUtc = DateTime.UtcNow;
            return;
        }
        catch (Exception ex)
        {
            completionActivity.SetTag("task.outcome", "Exception")
                .SetTag("exception.type", ex.GetBaseException().GetType().FullName ?? ex.GetBaseException().GetType().Name)
                .SetTag("exception.message", ex.GetBaseException().Message);
            ApplyTaskCompletionFailure(completedTaskId, ex);
            _lastCompletedTaskId = completedTaskId;
            _lastCompletionHandledAtUtc = DateTime.UtcNow;
            return;
        }

        ApplyTaskCompletionResult(completedTaskId, result);
        _lastCompletedTaskId = completedTaskId;
        _lastCompletionHandledAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Applies the semantic result returned by one completed task to the live session and shared scheduler stop
    /// state.
    /// </summary>
    private void ApplyTaskCompletionResult(ExecutionTaskId taskId, OperationResult result)
    {
        if (result.Outcome == ExecutionTaskOutcome.Completed)
        {
            _session.CompleteTaskLifecycle(taskId, ExecutionTaskOutcome.Completed);
            return;
        }

        if (result.Outcome == ExecutionTaskOutcome.Cancelled)
        {
            ApplyCancelledTaskCompletion(taskId);
            return;
        }

        if (result.Outcome == ExecutionTaskOutcome.Interrupted)
        {
            ApplyStopReason(SchedulerStopReason.InterruptedByTerminalOutcome);
            _session.InterruptTask(taskId);
            return;
        }

        _encounteredFailure = true;
        _session.FailScopeFromTask(taskId);
    }

    /// <summary>
    /// Applies the scheduler-wide cancellation or interruption policy to one completed task and all collateral runnable
    /// work.
    /// </summary>
    private void ApplyCancelledTaskCompletion(ExecutionTaskId taskId)
    {
        SchedulerStopReason stopReason = ResolveStopReasonOrDefault(SchedulerStopReason.UserCancelled);
        ApplyStopReason(stopReason);
        CompleteTaskWithStopOutcome(taskId, stopReason);
        CancelOutstandingTasks(stopReason);
    }

    /// <summary>
    /// Records a task exception in the task log and then fails the owning task scope with the most useful failure
    /// reason.
    /// </summary>
    private void ApplyTaskCompletionFailure(ExecutionTaskId taskId, Exception ex)
    {
        ExecutionTask failedTask = _session.GetTask(taskId);
        ILogger taskLogger = CreateTaskLogger(taskId);

        /* Task exceptions need to land in the task log with the full exception payload so the UI can surface the
           actual failure instead of only the scheduler's generic failure summary. */
        taskLogger.LogError(ex, "Execution task '{TaskTitle}' failed.", failedTask.Title);

        _encounteredFailure = true;
        _session.FailScopeFromTask(taskId);
    }

    /// <summary>
    /// Marks every remaining pending task as unsatisfied when no further work can be started.
    /// </summary>
    private void MarkUnsatisfiedTasks(IEnumerable<ExecutionTaskId> taskIds)
    {
        foreach (ExecutionTaskId taskId in taskIds)
        {
            ExecutionTaskState currentState = _session.GetTask(taskId).State;
            if (currentState == ExecutionTaskState.Completed)
            {
                continue;
            }

            _logger.LogWarning(
                "Marking task '{TaskPath}' ({TaskId}) unsatisfied.",
                _session.GetTaskDisplayPath(taskId),
                taskId);
            ExecutionLocks.UnregisterWaiter(taskId);
            _session.CompleteTaskWithOutcome(taskId, ExecutionTaskOutcome.Skipped);
        }
    }

    /// <summary>
    /// Marks all remaining runnable tasks after a global cancellation request.
    /// </summary>
    private void CancelOutstandingTasks(SchedulerStopReason stopReason, bool preserveRunningTerminalOutcomes = false)
    {
        foreach (ExecutionTask task in _session.Tasks)
        {
            ExecutionLocks.UnregisterWaiter(task.Id);
        }

        _session.CancelOutstandingSchedulableTasks(
            taskId => _session.GetTask(taskId).HasActiveExecution,
            userCancelled: stopReason == SchedulerStopReason.UserCancelled,
            preserveRunningTerminalOutcomes: preserveRunningTerminalOutcomes);
    }

    /// <summary>
    /// Records the most authoritative scheduler-wide stop reason seen so later token-driven shutdown paths map running
    /// work to the correct user-facing outcome.
    /// </summary>
    private void SetStopReason(SchedulerStopReason stopReason)
    {
        if (stopReason == SchedulerStopReason.None || _stopReason == stopReason)
        {
            return;
        }

        if (_stopReason == SchedulerStopReason.UserCancelled)
        {
            return;
        }

        _stopReason = stopReason;
    }

    /// <summary>
    /// Records the effective scheduler stop reason and mirrors user-triggered cancellation into the final session result.
    /// </summary>
    private void ApplyStopReason(SchedulerStopReason stopReason)
    {
        SetStopReason(stopReason);
        if (_stopReason == SchedulerStopReason.UserCancelled)
        {
            _encounteredCancellation = true;
        }
    }

    /// <summary>
    /// Reuses the currently recorded stop reason when one already exists so later completion paths stay consistent with
    /// the earlier scheduler-wide decision.
    /// </summary>
    private SchedulerStopReason ResolveStopReasonOrDefault(SchedulerStopReason fallbackStopReason)
    {
        return _stopReason == SchedulerStopReason.None
            ? fallbackStopReason
            : _stopReason;
    }

    /// <summary>
    /// Completes one running task with the lifecycle and semantic outcome implied by the scheduler-wide stop reason.
    /// </summary>
    private void CompleteTaskWithStopOutcome(ExecutionTaskId taskId, SchedulerStopReason stopReason)
    {
        _session.CompleteTaskLifecycle(
            taskId,
            stopReason == SchedulerStopReason.UserCancelled ? ExecutionTaskOutcome.Cancelled : ExecutionTaskOutcome.Interrupted);
    }

    /// <summary>
    /// Maps terminal task outcomes to the scheduler-wide stop reason that should be applied to collateral work.
    /// </summary>
    private static SchedulerStopReason ResolveStopReason(ExecutionTaskOutcome? outcome)
    {
        return outcome == ExecutionTaskOutcome.Cancelled
            ? SchedulerStopReason.UserCancelled
            : SchedulerStopReason.InterruptedByTerminalOutcome;
    }

    /// <summary>
    /// Returns whether the provided task-state event describes a terminal semantic outcome that should stop the shared
    /// execution token while body code is still active.
    /// </summary>
    private bool ShouldCancelExecutionForTerminalOutcome(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        if (outcome is not (ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Failed))
        {
            return false;
        }

        ExecutionTask task = _session.GetTask(taskId);
        return state == ExecutionTaskState.Running || task.State == ExecutionTaskState.Running || task.HasActiveExecution;
    }

    /// <summary>
    /// Converts externally forced terminal lifecycle states on active running tasks into semantic results without marking
    /// the task completed yet. This preserves the intended terminal outcome while task execution still unwinds under
    /// cancellation.
    /// </summary>
    private void NormalizeRunningTaskTerminalOutcome(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        if (outcome is not (ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Failed))
        {
            return;
        }

        ExecutionTask task = _session.GetTask(taskId);
        if (state == ExecutionTaskState.Completed || task.State == ExecutionTaskState.Completed || task.Outcome != null || !task.HasActiveExecution)
        {
            return;
        }

        _session.SetTaskOutcome(taskId, outcome);
    }

    /// <summary>
    /// Cancels the scheduler-owned execution token source once so every running task observes the same stop signal,
    /// regardless of whether it came from explicit user cancellation or a forced terminal task result.
    /// </summary>
    private void RequestExecutionCancellation()
    {
        lock (_executionCancellationSyncRoot)
        {
            if (_schedulerCancellationSource == null || _schedulerCancellationSource.IsCancellationRequested)
            {
                return;
            }

            _schedulerCancellationSource.Cancel();
        }
    }

    /// <summary>
    /// Returns the currently active execution handles by scanning the live task graph. The graph is small enough that the
    /// scheduler can derive this view directly from the tasks instead of maintaining a duplicated ownership registry.
    /// </summary>
    private Task<OperationResult>[] GetActiveExecutionTasksSnapshot()
    {
        return _session.Tasks
            .Select(task => task.ActiveExecutionTask)
            .Where(task => task != null)
            .Cast<Task<OperationResult>>()
            .ToArray();
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
    /// Returns whether this scheduler has no active worker but is still legitimately waiting for a global lock grant.
    /// </summary>
    private bool HasTasksWaitingForExecutionLocks()
    {
        return _session.Tasks.Any(task => task.State == ExecutionTaskState.AwaitingLock && !task.HasActiveExecution);
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
    /// Creates the reusable scheduler wake signal with asynchronous continuations so running tasks do not resume the
    /// scheduler inline while still holding graph-mutation call stacks.
    /// </summary>
    private static TaskCompletionSource<bool> CreateWorkAvailableSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Builds the top-level run result from semantic session outcomes rather than lifecycle state alone.
    /// </summary>
    private OperationResult BuildSessionResult()
    {
        return BuildResultFromTasks(_session.Tasks.ToList(), encounteredFailure: _encounteredFailure, encounteredCancellation: _encounteredCancellation);
    }

    /// <summary>
    /// Builds the child-operation result from the terminal states reached by the inserted child tasks.
    /// </summary>
    private OperationResult BuildChildOperationResult(Operation operation, IReadOnlyCollection<ExecutionTaskId> childTaskIds)
    {
        List<string> warnings = new();
        foreach (LogEntry entry in _session.LogStream.Entries.Where(entry => ExecutionTaskId.FromNullable(entry.TaskId) is ExecutionTaskId taskId && childTaskIds.Contains(taskId)))
        {
            if (entry.Verbosity == LogLevel.Warning)
            {
                warnings.Add(entry.Message);
            }
        }

        IReadOnlyList<ExecutionTask> childStates = childTaskIds
            .Select(taskId => _session.GetTask(taskId))
            .ToList();
        return BuildResultFromTasks(childStates, failOnWarning: operation.ShouldFailOnWarning(), warnings: warnings, treatSkippedAsFailure: true);
    }

    /// <summary>
    /// Builds an operation result from terminal task outcomes while preserving the scheduler's existing precedence rules
    /// for failure, interruption, cancellation, warning failure, and success.
    /// </summary>
    private static OperationResult BuildResultFromTasks(
        IReadOnlyList<ExecutionTask> tasks,
        bool failOnWarning = false,
        IReadOnlyList<string>? warnings = null,
        bool encounteredFailure = false,
        bool encounteredCancellation = false,
        bool treatSkippedAsFailure = false)
    {
        /* Run outcome is synthesized from semantic results. Lifecycle state alone only answers whether work is still
           active, so failure/cancellation detection must read Result instead of Status. */
        if (encounteredFailure || tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Failed) || (treatSkippedAsFailure && tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Skipped)))
        {
            return OperationResult.Failed();
        }

        if (tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Interrupted))
        {
            return OperationResult.Interrupted();
        }

        if (encounteredCancellation || tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Cancelled))
        {
            return OperationResult.Cancelled();
        }

        if (failOnWarning && warnings != null && warnings.Count > 0)
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
    /// Dispatches one already-resolved task body onto worker execution. The scheduler owns execution policy such as
    /// off-thread dispatch and lock lifetime, but it no longer reconstructs task selection or runtime context because the
    /// session start path already performed that shared task-local work.
    /// </summary>
    private void StartTaskExecutionAsync(
        ExecutionTask task,
        Func<Task<OperationResult>> executeAsync,
        TaskCompletionSource<OperationResult> completionSource)
    {
        /* Lock acquisition and the Running transition have already happened before dispatch, so this coroutine only owns
           executing the task body and reporting its eventual operation result. */
        _ = Task.Factory.StartNew(
            async () =>
            {
                try
                {
                    completionSource.TrySetResult(await ExecuteTaskBodyAsync(task, executeAsync).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    completionSource.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Executes one started task body while preserving task-state error and cancellation reporting at the scheduler
    /// boundary.
    /// </summary>
    private async Task<OperationResult> ExecuteTaskBodyAsync(ExecutionTask task, Func<Task<OperationResult>> executeAsync)
    {
        ILogger taskLogger = CreateTaskLogger(task.Id);
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.ExecuteTaskBody")
            .SetTag("task.id", task.Id.Value)
            .SetTag("task.title", task.Title);
        try
        {
            taskLogger.LogDebug("Starting task body '{TaskTitle}' ({TaskId}).", task.Title, task.Id);
            OperationResult result = await executeAsync().ConfigureAwait(false);
            activity.SetTag("task.outcome", result.Outcome.ToString());
            taskLogger.LogDebug("Finished task body '{TaskTitle}' ({TaskId}) with outcome '{Outcome}'.", task.Title, task.Id, result.Outcome);
            return result;
        }
        catch (OperationCanceledException)
        {
            activity.SetTag("task.outcome", ExecutionTaskOutcome.Cancelled.ToString());
            taskLogger.LogDebug("Task body '{TaskTitle}' ({TaskId}) observed cancellation.", task.Title, task.Id);
            throw;
        }
        catch (Exception ex)
        {
            activity.SetTag("task.outcome", "Exception")
                .SetTag("exception.type", ex.GetType().FullName ?? ex.GetType().Name)
                .SetTag("exception.message", ex.Message);

            taskLogger.LogDebug(ex, "Task body '{TaskTitle}' ({TaskId}) failed with an exception.", task.Title, task.Id);
            throw;
        }
    }

    /// <summary>
    /// Records one task-status transition in the session and mirrors it through the active task-state sink.
    /// </summary>
    private void SetState(ExecutionTaskId taskId, ExecutionTaskState state)
    {
        _session.SetTaskState(taskId, state);
        _taskStateSink?.SetTaskState(taskId, state);
        string taskPath = _session.GetTaskDisplayPath(taskId);
        _logger.LogDebug("Execution task '{TaskPath}' ({TaskId}) -> {State}.", taskPath, taskId, state);
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
