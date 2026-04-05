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

    private const string UnsatisfiedDependenciesReason = "Scheduler could not satisfy the remaining dependencies.";
    private readonly ILogger _logger;
    private readonly IExecutionTaskStateSink? _taskStateSink;
    private readonly ExecutionSession _session;
    private readonly object _syncRoot = new();
    private readonly object _workSignalSyncRoot = new();
    private readonly object _executionCancellationSyncRoot = new();
    private readonly Dictionary<ExecutionTaskId, Task<OperationResult>> _runningTasks = new();
    private readonly Dictionary<Task<OperationResult>, ExecutionTaskId> _taskOwners = new(TaskComparer.Instance);
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

                    CancelOutstandingTasks(_stopReason, runningTaskReason: ResolveStopReasonMessage(_stopReason));
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

                Task<OperationResult>[] runningTasksSnapshot = GetActiveRunningTasksSnapshot();

                if (runningTasksSnapshot.Length == 0)
                {
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
            Task<OperationResult>[] completedTasks = GetActiveRunningTasksSnapshot()
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
            "Finished inserted-task wait for child operation '{ChildOperation}'. Outcome='{Outcome}', failureReason='{FailureReason}'.",
            operation.OperationName,
            result.Outcome,
            result.FailureReason ?? string.Empty);
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
    /// Signals the scheduler when task-state changes may unblock downstream work without requiring a task completion to be
    /// the only wake-up source.
    /// </summary>
    private void HandleSessionTaskChanged(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome, string? statusReason)
    {
        NormalizeRunningTaskTerminalOutcome(taskId, state, outcome, statusReason);

        /* Running tasks only stop promptly when their shared execution token is cancelled. Session state alone is not
           enough because process-backed work observes cancellation through the task context token, so any externally
           forced terminal state must trigger the same cancellation path that user cancellation uses. */
        if (ShouldCancelExecutionForTerminalOutcome(taskId, state, outcome))
        {
            ApplyStopReason(ResolveStopReason(outcome));
            RequestExecutionCancellation();

            /* Forced terminal states should also immediately close out untouched queued work so the scheduler does not
               attempt to keep scheduling siblings while already-running tasks are unwinding. */
            CancelOutstandingTasks(_stopReason, runningTaskReason: statusReason, preserveRunningTerminalOutcomes: true);
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
            .SetTag("running.count", _runningTasks.Count)
            .SetTag("pending.count", _session.Tasks.Count(task => task.State == ExecutionTaskState.Pending));
        if (_lastCompletedTaskId != null)
        {
            readyActivity.SetTag("completed.task.id", _lastCompletedTaskId.Value.Value)
                .SetTag("completion_to_ready_ms", _lastCompletionHandledAtUtc == DateTime.MinValue ? "0" : (DateTime.UtcNow - _lastCompletionHandledAtUtc).TotalMilliseconds.ToString("0"));
        }

        while (true)
        {
            passCount += 1;
            bool startedThisPass = false;
            IReadOnlyList<ExecutionTask> readyTasks = _session.GetSchedulerReadyTasks();
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
                lock (_syncRoot)
                {
                    if (_runningTasks.ContainsKey(task.Id))
                    {
                        throw new InvalidOperationException($"Task '{task.Id}' is pending and scheduler-ready but is still tracked as running.");
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

                if (task.Outcome != null)
                {
                    throw new InvalidOperationException($"Task '{task.Id}' cannot start execution while it already has semantic outcome '{task.Outcome}'.");
                }

                IReadOnlyList<ExecutionLock> executionLocks = task.GetExecutionLocksForNextStart();
                if (!TryReserveExecutionLocks(task, executionLocks, out AsyncLockHandle lockHandle))
                {
                    /* Lock-blocked tasks remain Pending until the scheduler can reserve their full lock set. */
                    continue;
                }

                try
                {
                    TaskStartResult startResult = StartTaskExecutionAsync(task.Id, cancellationToken, lockHandle);
                    ExecutionTask startedTask = startResult.Task;
                    Task<OperationResult> runningTask = startResult.RunningTask;
                    lock (_syncRoot)
                    {
                        if (_runningTasks.TryGetValue(startedTask.Id, out Task<OperationResult>? existingTask))
                        {
                            throw new InvalidOperationException($"Task '{startedTask.Id}' cannot be started twice. Existing running task entry is still active ({existingTask.Status}).");
                        }

                        _runningTasks[startedTask.Id] = runningTask;
                        _taskOwners[runningTask] = startedTask.Id;
                    }

                    startedAny = true;
                    startedThisPass = true;
                    startedCount += 1;
                }
                catch
                {
                    lockHandle.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

        ValidateNoReadyTasksRemainPending();
        readyActivity.SetTag("started.count", startedCount);
        readyActivity.SetTag("pass.count", passCount);
        return startedAny;
    }

    /// <summary>
    /// Enforces that one scheduler pass reaches a fixpoint. If executable work is still ready after the pass ends, the
    /// scheduler has left runnable work behind and should fail immediately instead of waiting for an unrelated wake-up.
    /// </summary>
    private void ValidateNoReadyTasksRemainPending()
    {
        ExecutionTask? strandedTask = _session.GetSchedulerReadyTasks()
            .FirstOrDefault(task => task.GetExecutionLocksForNextStart().Count == 0);
        if (strandedTask == null)
        {
            return;
        }

        string taskPath = _session.GetTaskDisplayPath(strandedTask.Id);
        throw new InvalidOperationException($"Scheduler left runnable task '{taskPath}' ({strandedTask.Id}) pending even though its ancestor scopes are open, it declares no execution locks, and all dependencies are satisfied.");
    }

    /// <summary>
    /// Processes one completed task and updates live session state based on the task outcome.
    /// </summary>
    private async Task HandleCompletedTaskAsync(Task<OperationResult> completedTask)
    {
        ExecutionTaskId completedTaskId;
        lock (_syncRoot)
        {
            if (!_taskOwners.TryGetValue(completedTask, out completedTaskId))
            {
                throw new InvalidOperationException("Scheduler received completion for a task that is not tracked as running.");
            }

            _taskOwners.Remove(completedTask);
            if (!_runningTasks.Remove(completedTaskId))
            {
                throw new InvalidOperationException($"Scheduler lost running-task ownership for '{completedTaskId}' before its task completed.");
            }
        }

        using PerformanceActivityScope completionActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.HandleCompletedTask")
            .SetTag("task.id", completedTaskId.Value);

        try
        {
            OperationResult result = await completedTask.ConfigureAwait(false);
            completionActivity.SetTag("task.outcome", result.Outcome.ToString());
            ApplyTaskCompletionResult(completedTaskId, result);
        }
        catch (OperationCanceledException)
        {
            ApplyCancelledTaskCompletion(completedTaskId);
        }
        catch (Exception ex)
        {
            completionActivity.SetTag("task.outcome", "Exception")
                .SetTag("exception.type", ex.GetBaseException().GetType().FullName ?? ex.GetBaseException().GetType().Name)
                .SetTag("exception.message", ex.GetBaseException().Message);
            ApplyTaskCompletionFailure(completedTaskId, ex);
        }

        _session.RefreshSchedulerPendingReasons();
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
            _session.InterruptTask(taskId, result.FailureReason);
            return;
        }

        _encounteredFailure = true;
        _session.FailScopeFromTask(taskId, string.IsNullOrWhiteSpace(result.FailureReason)
            ? "The task returned failure."
            : result.FailureReason);
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
        CancelOutstandingTasks(stopReason, runningTaskReason: ResolveStopReasonMessage(stopReason));
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
        _session.FailScopeFromTask(taskId, GetFailureReason(ex));
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
                "Marking task '{TaskPath}' ({TaskId}) unsatisfied with reason '{Reason}'.",
                _session.GetTaskDisplayPath(taskId),
                taskId,
                UnsatisfiedDependenciesReason);
            _session.CompleteTaskWithOutcome(taskId, ExecutionTaskOutcome.Skipped, UnsatisfiedDependenciesReason);
        }
    }

    /// <summary>
    /// Marks all remaining runnable tasks after a global cancellation request.
    /// </summary>
    private void CancelOutstandingTasks(SchedulerStopReason stopReason, string? runningTaskReason = null, bool preserveRunningTerminalOutcomes = false)
    {
        _session.CancelOutstandingSchedulableTasks(
            IsTaskTrackedAsRunning,
            userCancelled: stopReason == SchedulerStopReason.UserCancelled,
            runningTaskReason: runningTaskReason ?? ResolveStopReasonMessage(stopReason),
            skippedTaskReason: stopReason == SchedulerStopReason.UserCancelled
                ? "Skipped because execution was cancelled."
                : "Skipped because execution was interrupted.",
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
            stopReason == SchedulerStopReason.UserCancelled ? ExecutionTaskOutcome.Cancelled : ExecutionTaskOutcome.Interrupted,
            ResolveStopReasonMessage(stopReason));
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
    /// Returns one user-facing reason string for the current scheduler-wide stop reason.
    /// </summary>
    private static string ResolveStopReasonMessage(SchedulerStopReason stopReason)
    {
        return stopReason == SchedulerStopReason.UserCancelled
            ? "Cancelled."
            : "Interrupted.";
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
        return state == ExecutionTaskState.Running || task.State == ExecutionTaskState.Running || IsTaskTrackedAsRunning(taskId);
    }

    /// <summary>
    /// Converts externally forced terminal lifecycle states on active running tasks into semantic results without marking
    /// the task completed yet. This preserves the intended terminal outcome while task execution still unwinds under
    /// cancellation.
    /// </summary>
    private void NormalizeRunningTaskTerminalOutcome(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome, string? statusReason)
    {
        if (outcome is not (ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Failed))
        {
            return;
        }

        ExecutionTask task = _session.GetTask(taskId);
        if (state == ExecutionTaskState.Completed || task.State == ExecutionTaskState.Completed || task.Outcome != null || !IsTaskTrackedAsRunning(taskId))
        {
            return;
        }

        _session.SetTaskOutcome(taskId, outcome, statusReason);
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
    /// Returns whether the scheduler still owns a live running task for the provided task id.
    /// </summary>
    private bool IsTaskTrackedAsRunning(ExecutionTaskId taskId)
    {
        lock (_syncRoot)
        {
            return _runningTasks.ContainsKey(taskId);
        }
    }

    /// <summary>
    /// Returns the currently running tasks. Scheduler progress now follows only dependency readiness, so there is no
    /// separate subset of capacity-consuming work.
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
    /// Returns the first non-empty status reason, optionally constrained to one semantic outcome.
    /// </summary>
    private static string? GetFirstStatusReason(IEnumerable<ExecutionTask> tasks, params ExecutionTaskOutcome[] outcomes)
    {
        IEnumerable<ExecutionTask> filteredTasks = outcomes.Length == 0
            ? tasks
            : tasks.Where(task => task.Outcome != null && outcomes.Contains(task.Outcome.Value));
        return filteredTasks
            .Select(task => task.StatusReason)
            .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
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
            return OperationResult.Failed(failureReason: GetFirstStatusReason(
                tasks,
                treatSkippedAsFailure
                    ? new[] { ExecutionTaskOutcome.Failed, ExecutionTaskOutcome.Skipped }
                    : new[] { ExecutionTaskOutcome.Failed }));
        }

        if (tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Interrupted))
        {
            return OperationResult.Interrupted(failureReason: GetFirstStatusReason(tasks, ExecutionTaskOutcome.Interrupted));
        }

        if (encounteredCancellation || tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Cancelled))
        {
            return OperationResult.Cancelled(failureReason: GetFirstStatusReason(tasks, ExecutionTaskOutcome.Cancelled));
        }

        if (failOnWarning && warnings != null && warnings.Count > 0)
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
    /// Dispatches one task execution onto the thread pool so work that does synchronous setup before its first await does
    /// not block the scheduler loop from starting other ready tasks in the same scheduling pass.
    /// </summary>
    private TaskStartResult StartTaskExecutionAsync(ExecutionTaskId taskId, CancellationToken cancellationToken, AsyncLockHandle lockHandle)
    {
        TaskStartResult startResult = _session.StartTaskAsync(taskId, CreateTaskLogger, cancellationToken, this);
        TaskCompletionSource<OperationResult> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(async _ =>
        {
            try
            {
                completionSource.TrySetResult(await ExecuteTaskAsync(startResult.Task.Id, startResult.RunningTask, lockHandle).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                completionSource.TrySetCanceled();
            }
            catch (Exception ex)
            {
                completionSource.TrySetException(ex);
            }
        });
        return new TaskStartResult(startResult.Task, completionSource.Task);
    }

    /// <summary>
    /// Executes one task while preserving task-state error and cancellation reporting at the scheduler boundary.
    /// </summary>
    private async Task<OperationResult> ExecuteTaskAsync(ExecutionTaskId taskId, Task<OperationResult> runningTask, AsyncLockHandle lockHandle)
    {
        ExecutionTask task = _session.GetTask(taskId);
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.ExecuteTaskBody")
            .SetTag("task.id", task.Id.Value)
            .SetTag("task.title", task.Title);
        try
        {
            await using AsyncLockHandle acquiredLockHandle = lockHandle;
            OperationResult result = await runningTask.ConfigureAwait(false);
            activity.SetTag("task.outcome", result.Outcome.ToString());
            return result;
        }
        catch (OperationCanceledException)
        {
            activity.SetTag("task.outcome", ExecutionTaskOutcome.Cancelled.ToString());
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
    /// Tries to reserve the declared execution locks for one task before the scheduler marks that task Running. This
    /// keeps lock-blocked tasks visibly Pending until body execution can begin immediately.
    /// </summary>
    private bool TryReserveExecutionLocks(ExecutionTask task, IReadOnlyList<ExecutionLock> executionLocks, out AsyncLockHandle lockHandle)
    {
        if (executionLocks.Count == 0)
        {
            lockHandle = AsyncLockHandle.Empty;
            return true;
        }

        string lockSummary = string.Join(", ", executionLocks.Select(executionLock => executionLock.Key));
        ILogger taskLogger = CreateTaskLogger(task.Id);
        if (!ExecutionLocks.TryAcquire(executionLocks, out IAsyncDisposable handle))
        {
            taskLogger.LogDebug("Execution lock(s) still busy for task '{TaskTitle}': {ExecutionLocks}", task.Title, lockSummary);
            lockHandle = AsyncLockHandle.Empty;
            return false;
        }

        taskLogger.LogInformation("Acquired execution lock(s): {ExecutionLocks}", lockSummary);
        lockHandle = new AsyncLockHandle(handle, taskLogger, lockSummary);
        return true;
    }

    /// <summary>
    /// Wraps one acquired lock handle so release logging stays paired with the same task that acquired it.
    /// </summary>
    private sealed class AsyncLockHandle : IAsyncDisposable
    {
        public static AsyncLockHandle Empty { get; } = new(null, null, null);

        private readonly IAsyncDisposable? _inner;
        private readonly ILogger? _logger;
        private readonly string? _summary;

        public AsyncLockHandle(IAsyncDisposable? inner, ILogger? logger, string? summary)
        {
            _inner = inner;
            _logger = logger;
            _summary = summary;
        }

        public async ValueTask DisposeAsync()
        {
            if (_inner != null)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }

            if (_logger != null && !string.IsNullOrWhiteSpace(_summary))
            {
                _logger.LogInformation("Released execution lock(s): {ExecutionLocks}", _summary);
            }
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
    private void SetState(ExecutionTaskId taskId, ExecutionTaskState state, string? reason = null)
    {
        _session.SetTaskState(taskId, state, reason);
        _taskStateSink?.SetTaskState(taskId, state, reason);
        string taskPath = _session.GetTaskDisplayPath(taskId);
        _logger.LogDebug("Execution task '{TaskPath}' ({TaskId}) -> {State}. {Reason}", taskPath, taskId, state, reason ?? string.Empty);
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
