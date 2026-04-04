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

    private const string WaitingForDependenciesReason = "Waiting for dependencies.";
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
    public async Task<OperationResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        using CancellationTokenSource schedulerCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _schedulerCancellationSource = schedulerCancellationSource;
        _executionCancellationToken = schedulerCancellationSource.Token;
        _session.TaskGraphChanged += HandleSessionWorkChanged;
        _session.TaskStateChanged += HandleSessionTaskChanged;
        InitializeSessionStates();
        try
        {
            while (true)
            {
                ResetWorkAvailableSignalIfCompleted();

                if (_executionCancellationToken.IsCancellationRequested)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        SetStopReason(SchedulerStopReason.UserCancelled);
                    }

                    if (_stopReason == SchedulerStopReason.UserCancelled)
                    {
                        _encounteredCancellation = true;
                    }

                    CancelOutstandingTasks(_stopReason, runningTaskReason: ResolveStopReasonMessage(_stopReason));
                    break;
                }

                bool handledCompletedTasks = await DrainCompletedRunningTasksAsync().ConfigureAwait(false);
                bool startedAny = StartReadyItems(_executionCancellationToken);
                handledCompletedTasks |= await DrainCompletedRunningTasksAsync().ConfigureAwait(false);
                IReadOnlyList<ExecutionTaskId> incompleteTaskIds = GetIncompleteExecutableTaskIds();
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

        /* Run outcome is synthesized from semantic results. Lifecycle state alone only answers whether work is still
           active, so failure/cancellation detection must read Result instead of Status. */
        if (_encounteredFailure || _session.Tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Failed))
        {
            string? failureReason = _session.Tasks
                .Select(task => task.StatusReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            return OperationResult.Failed(failureReason: failureReason);
        }

        if (_session.Tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Interrupted))
        {
            string? interruptionReason = _session.Tasks
                .Where(task => task.Outcome == ExecutionTaskOutcome.Interrupted)
                .Select(task => task.StatusReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            return OperationResult.Interrupted(failureReason: interruptionReason);
        }

        if (_encounteredCancellation || _session.Tasks.Any(task => task.Outcome == ExecutionTaskOutcome.Cancelled))
        {
            return OperationResult.Cancelled();
        }

        return OperationResult.Succeeded();
    }

    /// <summary>
    /// Processes every task body that has already completed so visible task state is published promptly instead of
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
    /// Ensures every current task in the live session starts in a runtime-ready state before scheduling begins.
    /// </summary>
    private void InitializeSessionStates()
    {
        foreach (ExecutionTask task in _session.Tasks)
        {
            if (!task.Enabled)
            {
                _session.CompleteTaskWithOutcome(task.Id, ExecutionTaskOutcome.Disabled, task.DisabledReason);
                continue;
            }

            if (task.ExecuteAsync == null)
            {
                continue;
            }

            if (task.State == ExecutionTaskState.Planned)
            {
                SetState(task.Id, ExecutionTaskState.Pending, task.DependsOn.Count > 0 ? WaitingForDependenciesReason : null);
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
    private void HandleSessionTaskChanged(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome, string? statusReason)
    {
        NormalizeRunningTaskTerminalOutcome(taskId, state, outcome, statusReason);

        /* Running body tasks only stop promptly when their shared execution token is cancelled. Session state alone is not
           enough because process-backed tasks observe cancellation through the task context token, so any externally
           forced terminal state must trigger the same cancellation path that user cancellation uses. */
        if (ShouldCancelExecutionForTerminalOutcome(taskId, state, outcome))
        {
            SetStopReason(ResolveStopReason(outcome));
            RequestExecutionCancellation();

            /* Interrupted remains a distinct run outcome from user cancellation, so the scheduler only records
               cancellation here when the terminal status itself is Cancelled. Interrupted task state is preserved and the
               final result still rolls up from semantic task outcomes after running body tasks unwind. */
            if (_stopReason == SchedulerStopReason.UserCancelled)
            {
                _encounteredCancellation = true;
            }

            /* Forced terminal states should also immediately close out untouched queued work so the scheduler does not
               attempt to keep scheduling siblings while already-running body tasks are unwinding. */
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
            List<ExecutionTask> readyTasks = GetSchedulerReadyTasks();
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

                IReadOnlyList<ExecutionLock> executionLocks = task.Operation.GetDeclaredExecutionLocks(new ValidatedOperationParameters(task.Title, task.OperationParameters, task.DeclaredOptionTypes));
                if (!TryReserveExecutionLocks(task, executionLocks, out AsyncLockHandle lockHandle))
                {
                    /* Lock-blocked tasks remain Pending until the scheduler can reserve their full lock set. */
                    continue;
                }

                try
                {
                    SetState(task.Id, ExecutionTaskState.Running);
                    ExecutionTaskContext context = new(task.Id, task.Title, CreateTaskLogger(task.Id), cancellationToken, new ValidatedOperationParameters(task.Title, task.OperationParameters, task.DeclaredOptionTypes), task.Operation, _session, this);
                    Task<OperationResult> runningTask = StartTaskBodyAsync(task, context, lockHandle);
                    lock (_syncRoot)
                    {
                        if (_runningTasks.TryGetValue(task.Id, out Task<OperationResult>? existingTask))
                        {
                            throw new InvalidOperationException($"Task '{task.Id}' cannot be started twice. Existing running task entry is still active ({existingTask.Status}).");
                        }

                        _runningTasks[task.Id] = runningTask;
                        _taskOwners[runningTask] = task.Id;
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
     /// Returns the executable tasks that are immediately runnable in the current live graph state.
     /// </summary>
    private List<ExecutionTask> GetSchedulerReadyTasks()
    {
        return _session.Tasks
            .Where(task => task.ExecuteAsync != null)
            .Where(task => task.State == ExecutionTaskState.Pending)
            .Where(AreAncestorScopesOpen)
            .Where(task => task.DependsOn.All(IsDependencySatisfied))
            .OrderBy(task => task.Id.Value, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Enforces that one scheduler pass reaches a fixpoint. If executable work is still ready after the pass ends, the
    /// scheduler has left runnable work behind and should fail immediately instead of waiting for an unrelated wake-up.
    /// </summary>
    private void ValidateNoReadyTasksRemainPending()
    {
        ExecutionTask? strandedTask = GetSchedulerReadyTasks()
            .FirstOrDefault(task => task.Operation.GetDeclaredExecutionLocks(new ValidatedOperationParameters(task.Title, task.OperationParameters, task.DeclaredOptionTypes)).Count == 0);
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
                throw new InvalidOperationException("Scheduler received completion for a task body that is not tracked as running.");
            }

            _taskOwners.Remove(completedTask);
            if (!_runningTasks.Remove(completedTaskId))
            {
                throw new InvalidOperationException($"Scheduler lost running-task ownership for '{completedTaskId}' before its task body completed.");
            }
        }

        using PerformanceActivityScope completionActivity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.HandleCompletedTask")
            .SetTag("task.id", completedTaskId.Value);

        try
        {
            OperationResult result = await completedTask.ConfigureAwait(false);
            completionActivity.SetTag("task.outcome", result.Outcome.ToString());

            if (result.Outcome == ExecutionTaskOutcome.Completed)
            {
                _session.CompleteTaskLifecycle(completedTaskId, ExecutionTaskOutcome.Completed);
            }
            else if (result.Outcome == ExecutionTaskOutcome.Cancelled)
            {
                SchedulerStopReason stopReason = _stopReason == SchedulerStopReason.None
                    ? SchedulerStopReason.UserCancelled
                    : _stopReason;
                SetStopReason(stopReason);
                if (stopReason == SchedulerStopReason.UserCancelled)
                {
                    _encounteredCancellation = true;
                }

                _session.CompleteTaskLifecycle(
                    completedTaskId,
                    stopReason == SchedulerStopReason.UserCancelled ? ExecutionTaskOutcome.Cancelled : ExecutionTaskOutcome.Interrupted,
                    ResolveStopReasonMessage(stopReason));
                CancelOutstandingTasks(stopReason, runningTaskReason: ResolveStopReasonMessage(stopReason));
            }
            else if (result.Outcome == ExecutionTaskOutcome.Interrupted)
            {
                SetStopReason(SchedulerStopReason.InterruptedByTerminalOutcome);
                _session.InterruptTask(completedTaskId, result.FailureReason);
            }
            else
            {
                _encounteredFailure = true;
                _session.FailScopeFromTask(completedTaskId, string.IsNullOrWhiteSpace(result.FailureReason)
                    ? "The task body returned failure."
                    : result.FailureReason);
            }
        }
        catch (OperationCanceledException)
        {
            SchedulerStopReason stopReason = _stopReason == SchedulerStopReason.None
                ? SchedulerStopReason.UserCancelled
                : _stopReason;
            SetStopReason(stopReason);
            if (stopReason == SchedulerStopReason.UserCancelled)
            {
                _encounteredCancellation = true;
            }

            _session.CompleteTaskLifecycle(
                completedTaskId,
                stopReason == SchedulerStopReason.UserCancelled ? ExecutionTaskOutcome.Cancelled : ExecutionTaskOutcome.Interrupted,
                ResolveStopReasonMessage(stopReason));
            CancelOutstandingTasks(stopReason, runningTaskReason: ResolveStopReasonMessage(stopReason));
        }
        catch (Exception ex)
        {
            ExecutionTask failedTask = _session.GetTask(completedTaskId);
            ILogger taskLogger = CreateTaskLogger(completedTaskId);
            Exception rootException = ex.GetBaseException();

            /* Body-task exceptions need to land in the task log with the full exception payload so the UI can surface the
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
            ExecutionTaskState currentState = task.State;
            if (currentState is ExecutionTaskState.Completed or ExecutionTaskState.Running)
            {
                continue;
            }

            if (task.Outcome != null)
            {
                throw new InvalidOperationException($"Task '{task.Id}' cannot return to pending readiness while it already has semantic outcome '{task.Outcome}'.");
            }

            bool waitingForDependencies = task.DependsOn.Any(dependencyId => !IsDependencySatisfied(dependencyId));
            bool waitingForParent = !AreAncestorScopesOpen(task);
            string? waitingReason = waitingForDependencies
                ? WaitingForDependenciesReason
                : waitingForParent
                    ? "Waiting for parent scope."
                    : null;
            SetState(task.Id, ExecutionTaskState.Pending, waitingReason);
        }
    }

    /// <summary>
    /// Returns whether every ancestor container for the provided task is structurally open. Container openness is based on
    /// dependency satisfaction and non-terminal lifecycle, not on whether the scope is visibly Running.
    /// </summary>
    private bool AreAncestorScopesOpen(ExecutionTask task)
    {
        ExecutionTask? currentTask = task.ParentId is ExecutionTaskId parentId
            ? _session.GetTask(parentId)
            : null;
        while (currentTask != null)
        {
            if (currentTask.State == ExecutionTaskState.Completed || currentTask.Outcome != null)
            {
                return false;
            }

            if (!currentTask.DependsOn.All(IsDependencySatisfied))
            {
                return false;
            }

            currentTask = currentTask.ParentId is ExecutionTaskId nextParentId
                ? _session.GetTask(nextParentId)
                : null;
        }

        return true;
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
    /// Returns whether a dependency is satisfied strongly enough for downstream work to start.
    /// </summary>
    private bool IsDependencySatisfied(ExecutionTaskId dependencyId)
    {
        ExecutionTask dependencyTask = _session.GetTask(dependencyId);
        /* Dependencies are satisfied only by successful or disabled semantic outcomes. A completed lifecycle with failed,
           cancelled, or skipped result still blocks downstream work. */
        if (dependencyTask.Outcome == ExecutionTaskOutcome.Completed)
        {
            return true;
        }

        if (dependencyTask.Outcome != ExecutionTaskOutcome.Disabled)
        {
            return false;
        }

        return dependencyTask.DependsOn.All(IsDependencySatisfied);
    }

    /// <summary>
    /// Marks all remaining runnable tasks after a global cancellation request.
    /// </summary>
    private void CancelOutstandingTasks(SchedulerStopReason stopReason, string? runningTaskReason = null, bool preserveRunningTerminalOutcomes = false)
    {
        foreach (ExecutionTask task in _session.Tasks.Where(task => task.ExecuteAsync != null))
        {
            ExecutionTaskState currentState = task.State;
            if (currentState == ExecutionTaskState.Completed)
            {
                continue;
            }

            bool taskBodyIsStillActive = currentState == ExecutionTaskState.Running || IsTaskTrackedAsRunning(task.Id);
            ExecutionTaskOutcome nextOutcome = taskBodyIsStillActive
                ? stopReason == SchedulerStopReason.UserCancelled
                    ? ExecutionTaskOutcome.Cancelled
                    : ExecutionTaskOutcome.Interrupted
                : ExecutionTaskOutcome.Skipped;
            string reason = taskBodyIsStillActive
                ? runningTaskReason ?? ResolveStopReasonMessage(stopReason)
                : stopReason == SchedulerStopReason.UserCancelled
                    ? "Skipped because execution was cancelled."
                    : "Skipped because execution was interrupted.";

            /* Externally forced terminal results such as Interrupted may already be recorded on running tasks while their
               body tasks continue unwinding. Completing lifecycle through CompleteTaskLifecycle preserves those stricter
               semantic results instead of flattening them back to Cancelled. */
            if (preserveRunningTerminalOutcomes && taskBodyIsStillActive)
            {
                _session.CompleteTaskLifecycle(task.Id, nextOutcome, reason);
                continue;
            }

            _session.CompleteTaskLifecycle(task.Id, nextOutcome, reason);
        }
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
    /// Converts externally forced terminal lifecycle states on active body tasks into semantic results without marking the
    /// task completed yet. This preserves the intended terminal outcome while the body still unwinds under
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
    /// Cancels the scheduler-owned execution token source once so every running task body observes the same stop signal,
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
    /// Returns whether the scheduler still owns a live body task for the provided task id.
    /// </summary>
    private bool IsTaskTrackedAsRunning(ExecutionTaskId taskId)
    {
        lock (_syncRoot)
        {
            return _runningTasks.ContainsKey(taskId);
        }
    }

    /// <summary>
    /// Returns the currently running task bodies. Scheduler progress now follows only dependency readiness, so there is
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
    /// Creates the reusable scheduler wake signal with asynchronous continuations so task bodies do not resume the
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
        if (childStates.Any(state => state.Outcome == ExecutionTaskOutcome.Cancelled))
        {
            string? cancellationReason = childStates
                .Where(state => state.Outcome == ExecutionTaskOutcome.Cancelled)
                .Select(state => state.StatusReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            return OperationResult.Cancelled(failureReason: cancellationReason);
        }

        if (childStates.Any(state => state.Outcome == ExecutionTaskOutcome.Interrupted))
        {
            string? interruptionReason = childStates
                .Where(state => state.Outcome == ExecutionTaskOutcome.Interrupted)
                .Select(state => state.StatusReason)
                .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
            return OperationResult.Interrupted(failureReason: interruptionReason);
        }

        if (childStates.Any(state => state.Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Skipped))
        {
            string? failureReason = childStates
                .Where(state => state.Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Skipped)
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
    /// Dispatches one task body onto the thread pool so bodies that do synchronous work before their first await do
    /// not block the scheduler loop from starting other ready work in the same scheduling pass.
    /// </summary>
    private Task<OperationResult> StartTaskBodyAsync(ExecutionTask task, ExecutionTaskContext context, AsyncLockHandle lockHandle)
    {
        TaskCompletionSource<OperationResult> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(async _ =>
        {
            try
            {
                completionSource.TrySetResult(await ExecuteTaskBodyAsync(task, context, lockHandle).ConfigureAwait(false));
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
        return completionSource.Task;
    }

    /// <summary>
     /// Executes one task body while preserving task-state error and cancellation reporting at the scheduler boundary.
     /// </summary>
    private async Task<OperationResult> ExecuteTaskBodyAsync(ExecutionTask task, ExecutionTaskContext context, AsyncLockHandle lockHandle)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionPlanScheduler.ExecuteTaskBody")
            .SetTag("task.id", task.Id.Value)
            .SetTag("task.title", task.Title);
        try
        {
            await using AsyncLockHandle acquiredLockHandle = lockHandle;
            OperationResult result = await task.ExecuteAsync!(context).ConfigureAwait(false);
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
    /// Wraps one acquired lock handle so release logging stays paired with the same task body that acquired it.
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
