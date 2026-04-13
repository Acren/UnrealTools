using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents one live execution session. The session owns graph coordination, locking, and session-wide lifecycle
/// state, while each execution task owns its own mutable runtime state, subtree metrics, graph links, and task-scoped
/// log stream.
/// </summary>
public sealed class ExecutionSession
{
    private readonly TaskCompletionSource<bool> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ExecutionTask? _rootTask;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    /* The live task graph needs one session-scoped synchronization boundary, but graph traversal happens much more
       often than graph mutation. A recursive reader/writer lock keeps traversal coherent while still letting the
       scheduler's write-side updates break through tight concurrent read loops such as the inserted-child traversal
       regression scenario. */
    private readonly ReaderWriterLockSlim _graphLock = new(LockRecursionPolicy.SupportsRecursion);
    /* Some runtime follow-up work, such as session-level notifications, must happen only after the outermost graph
       write scope releases the lock. Keep those callbacks generic so any future post-write action can reuse the same
       deferral path instead of adding one-off state for each notification type. */
    private readonly List<Action> _afterGraphWriteReleasedCallbacks = new();
    private readonly SessionLogger _sessionLogger;
    private Task<OperationResult>? _currentTask;
    /* Recursive write scopes are allowed, so only the outermost write-lock owner is allowed to drain the queued
       task-state notifications after releasing the graph lock. */
    private int _graphWriteLockDepth;

    /// <summary>
    /// Creates an execution session around a shared log stream and the authored plan it will execute.
    /// </summary>
    public ExecutionSession(ILogStream logStream, ExecutionPlan plan)
    {
        Id = ExecutionSessionId.New();
        LogStream = logStream ?? throw new ArgumentNullException(nameof(logStream));
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        StartedAt = DateTimeOffset.Now;
        _sessionLogger = new SessionLogger(this);

        InitializeFromPlan(plan);
    }

    public event Action<ExecutionTaskId, ExecutionTaskState, ExecutionTaskOutcome?>? TaskStateChanged;
    public event Action? TaskGraphChanged;

    public ExecutionSessionId Id { get; }

    public ILogStream LogStream { get; }

    /// <summary>
    /// Gets the live task graph coordinated by this session. Sessions clone plan tasks when they start so runtime
    /// mutations never modify preview plan objects, and the cloned task objects then own all per-task runtime data while
    /// the session serializes graph reads and writes through one lock.
    /// </summary>
    public IReadOnlyList<ExecutionTask> Tasks => WithGraphReadLock(() => _rootTask?.GetAllTasks() ?? Array.Empty<ExecutionTask>());

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>
    /// Gets whether the host-visible session is still active. Session running state follows the authoritative completion
    /// signal instead of keeping a second mutable flag that can drift from task completion.
    /// </summary>
    public bool IsRunning => !Completion.IsCompleted;

    /// <summary>
    /// Gets the task that completes when the runtime has fully finalized the session outcome and running state.
    /// </summary>
    public Task Completion => _completionSource.Task;

    /// <summary>
    /// Gets the semantic result of the root task, which is the semantic result of the overall session.
    /// </summary>
    public ExecutionTaskOutcome? Outcome => _rootTask == null ? null : RootTask.Outcome;

    public bool? Success
    {
        get => Outcome == null ? null : Outcome == ExecutionTaskOutcome.Completed;
    }

    public string OperationName => RootTask.Operation.OperationName;

    public string TargetName => RootTask.OperationParameters.Target?.DisplayName ?? string.Empty;

    /// <summary>
    /// Gets the session-scoped logger that writes into this session's buffered log streams while still mirroring output
    /// to the process-wide application logger.
    /// </summary>
    public ILogger Logger => _sessionLogger;

    /// <summary>
    /// Executes this live session through the shared scheduler after preparing the top-level runtime logger and output
    /// directory state.
    /// </summary>
    public async Task RunAsync()
    {
        if (_currentTask != null)
        {
            throw new InvalidOperationException("Task is already running");
        }

        ExecutionTask rootTask = RootTask;
        Operation operation = rootTask.Operation;
        OperationParameters operationParameters = rootTask.OperationParameters;

        /* Top-level execution owns the concrete output directory lifecycle so each run starts from a clean workspace
           before any schedulable task body begins. */
        string outputPath = operation.GetOutputPath(operationParameters);
        if (Directory.Exists(outputPath))
        {
            Directory.Delete(outputPath, recursive: true);
        }

        EventStreamLogger eventLogger = CreateAggregatingLogger();
        string? requirementsError = operation.CheckRequirementsSatisfied(operationParameters);
        if (requirementsError != null)
        {
            /* Requirement failures are normal failed results, not exceptions. Log the concrete validation message here so
               parent bodies and the UI can surface the actual reason even when no task body ever starts. */
            eventLogger.LogError("Operation '{OperationName}' requirements failed: {RequirementsError}", operation.OperationName, requirementsError);
            CompleteRootTaskIfNeeded(ExecutionTaskOutcome.Failed);
            CompleteExecution();
            return;
        }

        using IDisposable operationTimingScope = eventLogger.BeginSection(operation.OperationName);
        _currentTask = ExecuteOnThread(() => new ExecutionPlanScheduler(eventLogger, this).ExecuteAsync(_cancellationTokenSource.Token));
        try
        {
            OperationResult result = await _currentTask.ConfigureAwait(false);
            using PerformanceActivityScope finalizeActivity = PerformanceTelemetry.StartActivity("ExecutionSession.Run.FinalizeOutcome")
                .SetTag("operation.name", operation.OperationName)
                .SetTag("incoming.result", result.Outcome.ToString());
            OperationResult finalizedResult = FinalizeOutcome(result, eventLogger);
            CompleteRootTaskIfNeeded(finalizedResult.Outcome);
        }
        catch (OperationCanceledException)
        {
            CompleteRootTaskIfNeeded(ExecutionTaskOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            AddLogEntry(new LogEntry
            {
                SessionId = Id.Value,
                Message = ex.ToString(),
                Verbosity = LogLevel.Error
            });

            /* Any fatal scheduler/session exception must leave the live graph in one coherent terminal state. Mark the
               root failed, cancel running work, and force every remaining task to a terminal outcome before the session
               completion signal is published so the UI never shows a failed root beside still-running descendants. */
            FailOutstandingTasksAfterFatalException();
            throw;
        }
        finally
        {
            _sessionLogger.LogDebug("'{OperationName}' task ended", operation.OperationName);
            _currentTask = null;
            CompleteExecution();
        }
    }

    /// <summary>
    /// Gets the single root task that represents the overall task graph for this session.
    /// </summary>
    public ExecutionTask RootTask => WithGraphReadLock(() => _rootTask ?? throw new InvalidOperationException("Session has no root task."));

    public IReadOnlyList<ExecutionTaskId> GetTaskSubtreeIds(ExecutionTaskId taskId)
    {
        return WithGraphReadLock(() => GetTaskCore(taskId).GetSubtreeIds());
    }

    /// <summary>
    /// Returns the current metrics for one task subtree or for the whole session. Task metrics come from the cached
    /// subtree state owned by the live task objects, while session metrics come from session-wide counters.
    /// </summary>
    public ExecutionTaskMetrics GetTaskMetrics(ExecutionTaskId? taskId, DateTimeOffset? now = null)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.GetTaskMetrics")
            .SetTag("task.id", taskId?.Value ?? string.Empty);

        if (taskId == null)
        {
            return WithGraphReadLock(() =>
            {
                ExecutionTaskMetrics rootMetrics = (_rootTask ?? throw new InvalidOperationException("Session has no root task."))
                    .GetSubtreeMetrics(now);
                activity.SetTag("scope", "session")
                    .SetTag("warning.count", rootMetrics.WarningCount)
                    .SetTag("error.count", rootMetrics.ErrorCount);
                return new ExecutionTaskMetrics(GetSessionDuration(now), rootMetrics.WarningCount, rootMetrics.ErrorCount);
            });
        }

        return WithGraphReadLock(() =>
        {
            ExecutionTask task = GetTaskCore(taskId.Value);
            ExecutionTaskMetrics metrics = task.GetSubtreeMetrics(now);
            activity.SetTag("scope", "subtree")
                .SetTag("warning.count", metrics.WarningCount)
                .SetTag("error.count", metrics.ErrorCount)
                .SetTag("duration.available", metrics.Duration != null);
            return metrics;
        });
    }

    /// <summary>
    /// Returns the current subtree duration for one task from the cached timing basis owned by that task.
    /// </summary>
    public TimeSpan? GetTaskDuration(ExecutionTaskId? taskId, DateTimeOffset? now = null)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.GetTaskDuration")
            .SetTag("task.id", taskId?.Value ?? string.Empty);

        if (taskId == null)
        {
            activity.SetTag("scope", "none")
                .SetTag("duration.available", false);
            return null;
        }

        return WithGraphReadLock(() =>
        {
            TimeSpan? duration = GetTaskCore(taskId.Value).GetSubtreeDuration(now);
            activity.SetTag("scope", "subtree")
                .SetTag("duration.available", duration != null);
            if (duration != null)
            {
                activity.SetTag("duration_ms", duration.Value.TotalMilliseconds.ToString("0.###"));
            }

            return duration;
        });
    }

    /// <summary>
    /// Appends one log entry to the session stream and updates the affected session/task metrics caches from that same
    /// source event instead of rescanning buffered logs later.
    /// </summary>
    public void AddLogEntry(LogEntry entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        LogStream.Add(entry);
        (int warningDelta, int errorDelta) = GetLogCountDeltas(entry);
        ExecutionTaskId? taskId = ExecutionTaskId.FromNullable(entry.TaskId);

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.AddLogEntry")
            .SetTag("task.id", taskId?.Value ?? string.Empty)
            .SetTag("warning.delta", warningDelta)
            .SetTag("error.delta", errorDelta);
        WithGraphWriteLock(() =>
        {
            ExecutionTask task = taskId == null
                ? _rootTask ?? throw new InvalidOperationException("Session has no root task.")
                : GetTaskCore(taskId.Value);
            task.LogStream.Add(entry);
            ApplyTaskLogMetrics(task, warningDelta, errorDelta);
        });
    }

    /// <summary>
    /// Clears the cached warning/error metrics that are derived from the session log and task-scoped subtree log counts.
    /// The owning task objects keep the per-task counters; the session only coordinates resetting them.
    /// </summary>
    public void ResetCachedLogMetrics()
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.ResetCachedLogMetrics");
        WithGraphWriteLock(() =>
        {
            if (_rootTask == null)
            {
                activity.SetTag("task.count", 0);
                return;
            }

            IReadOnlyList<ExecutionTask> tasks = _rootTask.GetAllTasks();
            foreach (ExecutionTask task in tasks)
            {
                task.ResetSubtreeLogCounts();
            }

            activity.SetTag("task.count", tasks.Count);
        });
    }

    public BufferedLogStream? GetTaskLogStream(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        /* Tree-walk lookup is acceptable here because this method is called infrequently for UI-driven log panel
           selection, not on the scheduler hot path. The shared graph lock still keeps the tree stable while the walk
           runs. */
        return WithGraphReadLock(() => _rootTask?.FindTask(taskId.Value)?.LogStream);
    }

    public void SetTaskState(ExecutionTaskId taskId, ExecutionTaskState state)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.SetTaskState")
            .SetTag("task.id", taskId.Value)
            .SetTag("task.state", state.ToString());
        WithGraphWriteLock(() =>
        {
            /* Public state writes update execution state only. Semantic outcome is derived separately so direct
               execution, child aggregation, and failure propagation can share one runtime model without overloading one
               enum with two meanings. */
            SetTaskStateCore(taskId, state);
            RefreshAncestorTaskStates(taskId);
        });
    }

    /// <summary>
    /// Fails one task scope, skips untouched descendants, interrupts started sibling subtrees, and propagates the failure
    /// up through ancestors so scope status follows hierarchy rather than only explicit dependency edges.
    /// </summary>
    public void FailScopeFromTask(ExecutionTaskId failedTaskId)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.FailScopeFromTask")
            .SetTag("task.id", failedTaskId.Value);
        WithGraphWriteLock(() =>
        {
            /* A failed subtree keeps lifecycle and semantic outcome separate: the failing task completes with
               Result=Failed, untouched descendants complete with Result=Skipped, and sibling subtrees that already began
               real work are marked Interrupted while still allowing active descendant work to unwind. */
            ExecutionTask failedTask = GetTaskCore(failedTaskId);
            CompleteTaskWithOutcomeCore(failedTaskId, ExecutionTaskOutcome.Failed);
            failedTask.SkipUnfinishedDescendants();

            ExecutionTask currentAncestor = failedTask;
            ExecutionTaskId failedSiblingRootId = failedTaskId;
            while (currentAncestor.Parent != null)
            {
                currentAncestor = currentAncestor.Parent;
                foreach (ExecutionTask child in currentAncestor.Children)
                {
                    if (child.Id == failedSiblingRootId)
                    {
                        continue;
                    }

                    child.InterruptSiblingSubtree(failedSiblingRootId);
                }

                CompleteTaskWithOutcomeCore(currentAncestor.Id, ExecutionTaskOutcome.Failed);
                failedSiblingRootId = currentAncestor.Id;
            }
        });
    }

    /// <summary>
    /// Inserts one child plan beneath the currently executing task in the live session graph using the shared
    /// task-insertion path.
    /// </summary>
    public ChildTaskMergeResult MergeChildTasks(ExecutionTaskId parentTaskId, ExecutionPlan childPlan, bool hideChildRootInGraph = false)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.MergeChildTasks")
            .SetTag("parent.task.id", parentTaskId.Value)
            .SetTag("child.plan.title", childPlan.Title)
            .SetTag("child.plan.task.count", childPlan.Tasks.Count)
            .SetTag("child.root.hidden", hideChildRootInGraph);

        InsertedExecutionTasks insertedTasks;
        using (PerformanceActivityScope insertActivity = PerformanceTelemetry.StartActivity("ExecutionSession.MergeChildTasks.InsertUnderParent"))
        {
            ExecutionTask childRoot = childPlan.Tasks.Single(task => task.ParentId == null);
            ChildOperationRootOverrides? rootOverrides = hideChildRootInGraph
                ? new ChildOperationRootOverrides(childRoot.Title, childRoot.Description, isHiddenInGraph: true)
                : null;
            insertedTasks = ExecutionTaskInsertion.InsertUnderParent(childPlan, parentTaskId, rootOverrides);
            insertActivity.SetTag("inserted.task.count", insertedTasks.Tasks.Count)
                .SetTag("inserted.root.id", insertedTasks.RootTaskId.Value);
        }

        ChildTaskMergeResult mergeResult;
        using (PerformanceActivityScope addTasksActivity = PerformanceTelemetry.StartActivity("ExecutionSession.MergeChildTasks.AddTasks"))
        {
            mergeResult = WithGraphWriteLock(() =>
            {
                _ = GetTaskCore(parentTaskId);
                List<ExecutionTaskId> insertedTaskIds = new();

                /* Dependency IDs copy through cloning as value types, so inserted tasks already carry the correct
                   dependency set from the child plan — no re-wiring needed. */
                foreach (ExecutionTask insertedTask in insertedTasks.Tasks)
                {
                    AddTaskCore(insertedTask);
                    insertedTaskIds.Add(insertedTask.Id);
                }

                RebuildSubtreeSchedulingRollups(insertedTasks.Tasks);
                RefreshTaskAndAncestorTimingMetrics(GetTaskCore(insertedTasks.RootTaskId));
                RefreshAncestorTaskStates(insertedTasks.RootTaskId);

                addTasksActivity.SetTag("inserted.task.count", insertedTaskIds.Count);
                return new ChildTaskMergeResult(GetTaskCore(insertedTasks.RootTaskId), insertedTasks.Tasks, insertedTaskIds);
            });
        }

        /* Notify the scheduler only after the entire child subtree has been attached under the graph lock so the next
           traversal sees a coherent post-merge tree. */
        TaskGraphChanged?.Invoke();
        activity.SetTag("inserted.root.title", mergeResult.RootTask.Title)
            .SetTag("inserted.task.count", mergeResult.InsertedTaskIds.Count);
        return mergeResult;
    }

    /// <summary>
    /// Builds, attaches, and waits for one nested child operation within the current live session graph.
    /// </summary>
    internal async Task<OperationResult> RunChildOperationAsync(Operation operation, OperationParameters operationParameters, ExecutionTaskContext parentContext, ExecutionPlanScheduler scheduler, bool hideChildOperationRootInGraph = false)
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

        if (scheduler == null)
        {
            throw new ArgumentNullException(nameof(scheduler));
        }

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.RunChildOperation")
            .SetTag("parent.task.id", parentContext.TaskId.Value)
            .SetTag("parent.task.title", parentContext.Title)
            .SetTag("child.operation", operation.OperationName)
            .SetTag("target.type", operationParameters.Target?.GetType().Name ?? string.Empty)
            .SetTag("child.root.hidden", hideChildOperationRootInGraph);

        /* Child-operation orchestration can fail before any inserted task ever appears in the live graph, so log each
           stage explicitly to show whether the child plan was built, merged, awaited, and completed. */
        parentContext.Logger.LogDebug(
            "Starting child operation '{ChildOperation}' beneath task '{ParentTaskTitle}' ({ParentTaskId}) for target '{TargetDisplayName}'.",
            operation.OperationName,
            parentContext.Title,
            parentContext.TaskId,
            operationParameters.Target?.DisplayName ?? string.Empty);

        ExecutionPlan childPlan;
        using (PerformanceActivityScope buildPlanActivity = PerformanceTelemetry.StartActivity("ExecutionSession.RunChildOperation.BuildPlan"))
        {
            childPlan = ExecutionPlanFactory.BuildWrappedPlan(operation, operationParameters, parentContext.Logger)
                ?? throw new InvalidOperationException($"Operation '{operation.OperationName}' did not produce an execution plan.");
            buildPlanActivity.SetTag("child.task.count", childPlan.Tasks.Count)
                .SetTag("child.title", childPlan.Title);

            parentContext.Logger.LogDebug(
                "Built child plan '{ChildPlanTitle}' for operation '{ChildOperation}' with {ChildTaskCount} task(s).",
                childPlan.Title,
                operation.OperationName,
                childPlan.Tasks.Count);
        }

        ChildTaskMergeResult mergeResult;
        using (PerformanceActivityScope mergeActivity = PerformanceTelemetry.StartActivity("ExecutionSession.RunChildOperation.MergeTasks"))
        {
            mergeResult = MergeChildTasks(parentContext.TaskId, childPlan, hideChildOperationRootInGraph);
            mergeActivity.SetTag("inserted.task.count", mergeResult.InsertedTaskIds.Count)
                .SetTag("inserted.root.id", mergeResult.RootTask.Id.Value)
                .SetTag("inserted.root.title", mergeResult.RootTask.Title);

            parentContext.Logger.LogDebug(
                "Merged child operation '{ChildOperation}' beneath task '{ParentTaskId}'. Root='{InsertedRootTitle}' ({InsertedRootId}), inserted task ids=[{InsertedTaskIds}]",
                operation.OperationName,
                parentContext.TaskId,
                mergeResult.RootTask.Title,
                mergeResult.RootTask.Id,
                string.Join(", ", mergeResult.InsertedTaskIds.Select(taskId => taskId.Value)));
        }

        parentContext.Logger.LogDebug(
            "Waiting for child operation '{ChildOperation}' inserted tasks to complete beneath task '{ParentTaskId}'.",
            operation.OperationName,
            parentContext.TaskId);
        OperationResult result = await scheduler.WaitForInsertedChildTasksAsync(operation, parentContext, mergeResult).ConfigureAwait(false);
        activity.SetTag("result.outcome", result.Outcome.ToString())
            .SetTag("result.success", result.Success);
        parentContext.Logger.LogDebug(
            "Child operation '{ChildOperation}' finished beneath task '{ParentTaskId}' with outcome '{Outcome}'.",
            operation.OperationName,
            parentContext.TaskId,
            result.Outcome);
        return result;
    }

    /// <summary>
    /// Blocks until the supplied task ids reach terminal runtime states.
    /// </summary>
    public Task WaitForTaskCompletionAsync(IReadOnlyCollection<ExecutionTaskId> taskIds, CancellationToken cancellationToken)
    {
        if (taskIds == null)
        {
            throw new ArgumentNullException(nameof(taskIds));
        }

        if (taskIds.Count == 0 || taskIds.All(IsTaskTerminal))
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource<bool> completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HashSet<ExecutionTaskId> remainingTaskIds = new(taskIds.Where(taskId => !IsTaskTerminal(taskId)));
        CancellationTokenRegistration cancellationRegistration = default;

        void CompleteIfFinished(ExecutionTaskId taskId)
        {
            if (!remainingTaskIds.Remove(taskId) || remainingTaskIds.Count > 0)
            {
                return;
            }

            TaskStateChanged -= OnTaskStateChanged;
            cancellationRegistration.Dispose();
            completionSource.TrySetResult(true);
        }

        void OnTaskStateChanged(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? __)
        {
            /* Session task-state subscribers can run while the graph write lock is still held, so this callback must not
               perform any additional session graph reads before it acquires its own local completion bookkeeping lock.
               The published state already tells us whether the task reached the terminal lifecycle state we care about. */
            if (state != ExecutionTaskState.Completed)
            {
                return;
            }

            lock (remainingTaskIds)
            {
                CompleteIfFinished(taskId);
            }
        }

        cancellationRegistration = cancellationToken.Register(() =>
        {
            TaskStateChanged -= OnTaskStateChanged;
            completionSource.TrySetCanceled(cancellationToken);
        });

        TaskStateChanged += OnTaskStateChanged;

        /* Do the post-subscription terminal sweep without holding the local remaining-task lock across session graph
           reads. Holding that lock while calling `IsTaskTerminal` can deadlock against the completion callback above,
           which is allowed to run under the graph write lock and then needs this same local lock to remove the task. */
        foreach (ExecutionTaskId taskId in remainingTaskIds.ToList())
        {
            if (!IsTaskTerminal(taskId))
            {
                continue;
            }

            lock (remainingTaskIds)
            {
                CompleteIfFinished(taskId);
            }
        }

        return completionSource.Task;
    }

    public Task CancelAsync()
    {
        Task<OperationResult>? currentTask = _currentTask;
        if (currentTask == null)
        {
            return Task.CompletedTask;
        }

        if (_cancellationTokenSource.IsCancellationRequested)
        {
            _sessionLogger.LogDebug("Cancellation was already requested for operation '{OperationName}'.", OperationName);
            return currentTask;
        }

        _sessionLogger.LogWarning("Cancelling operation '{OperationName}'", OperationName);
        _cancellationTokenSource.Cancel();
        return currentTask;
    }

    /// <summary>
    /// Returns the schedulable tasks that are immediately ready to start in the current live graph state.
    /// </summary>
    internal IReadOnlyList<ExecutionTask> GetSchedulerReadyTasks()
    {
        return WithGraphReadLock(() => RootTask.GetSchedulerReadyBranchRoots());
    }

    /// <summary>
    /// Refreshes pending-state reasons for tasks that participate directly in scheduler execution.
    /// </summary>
    internal void RefreshSchedulerPendingReasons()
    {
        using PerformanceActivityScope refreshActivity = PerformanceTelemetry.StartActivity("ExecutionSession.RefreshSchedulerPendingReasons");
        WithGraphWriteLock(() =>
        {
            IReadOnlyList<ExecutionTask> tasks = Tasks;
            int changedTaskCount = 0;
            refreshActivity.SetTag("task.count", tasks.Count);

            foreach (ExecutionTask task in tasks)
            {
                ExecutionTaskState currentState = task.State;
                if (currentState is ExecutionTaskState.Completed or ExecutionTaskState.Running or ExecutionTaskState.AwaitingLock or ExecutionTaskState.AwaitingDependency)
                {
                    continue;
                }

                if (task.Outcome != null)
                {
                    throw new InvalidOperationException($"Task '{task.Id}' cannot return to pending readiness while it already has semantic outcome '{task.Outcome}'.");
                }

                TaskStartState startState = task.GetTaskStartState();
                if (startState is TaskStartState.NoStartableWork or TaskStartState.Running or TaskStartState.AwaitingLock)
                {
                    continue;
                }

                /* Let the task compute its own visible status from the same rollup path ancestor containers use
                   so queued-vs-awaiting-dependency semantics stay centralized in one task-owned projection. */
                (ExecutionTaskState nextState, _) = task.ComputeRolledUpStateFromChildren();
                if (task.State != nextState)
                {
                    changedTaskCount += 1;
                }

                SetTaskStateCore(task.Id, nextState);
                RefreshAncestorTaskStates(task.Id);
            }

            refreshActivity.SetTag("changed.task.count", changedTaskCount);
        });
    }

    /// <summary>
    /// Returns every scheduler-managed task that has not yet reached a terminal runtime state.
    /// </summary>
    internal IReadOnlyList<ExecutionTaskId> GetIncompleteSchedulableTaskIds()
    {
        return WithGraphReadLock(() => Tasks
            .Where(task => task.HasActiveExecution || task.GetTaskStartState() != TaskStartState.NoStartableWork)
            .Where(task => task.HasActiveExecution || task.ParentId == null || GetTaskCore(task.ParentId.Value).GetTaskStartState() == TaskStartState.NoStartableWork)
            .Select(task => task.Id)
            .Where(taskId => !IsTaskTerminal(taskId))
            .ToList());
    }

    /// <summary>
    /// Returns whether forced terminalization must treat this task as in-progress work instead of untouched queued work.
    /// The visible task state is the primary lifecycle contract here, while the active-execution fallbacks preserve
    /// correctness during the narrow admission window before the visible state catches up.
    /// </summary>
    private static bool IsTaskInProgressForForcedTerminalization(ExecutionTask task, Func<ExecutionTaskId, bool>? isTaskTrackedAsRunning = null)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        return task.IsInProgress
            || task.HasActiveExecution
            || (isTaskTrackedAsRunning?.Invoke(task.Id) ?? false);
    }

    /// <summary>
    /// Applies a scheduler-wide stop outcome to every non-terminal task in the live graph.
    /// Started work becomes Cancelled or Interrupted, while untouched queued work remains Skipped.
    /// </summary>
    internal void CancelOutstandingSchedulableTasks(
        Func<ExecutionTaskId, bool> isTaskTrackedAsRunning,
        bool userCancelled,
        bool preserveRunningTerminalOutcomes)
    {
        if (isTaskTrackedAsRunning == null)
        {
            throw new ArgumentNullException(nameof(isTaskTrackedAsRunning));
        }

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.CancelOutstandingSchedulableTasks")
            .SetTag("user.cancelled", userCancelled)
            .SetTag("preserve.running.terminal.outcomes", preserveRunningTerminalOutcomes);
        WithGraphWriteLock(() =>
        {
            /* Finalize in-progress tasks before untouched descendants so a started container scope can keep its cancelled
               or interrupted outcome and then propagate skipped completion only to the descendants that never ran.
               Within each in-progress/untouched bucket, finish deeper tasks first so container rollups still observe
               already-terminal descendants where possible. */
            List<(ExecutionTask task, bool isInProgress)> outstandingTasks = Tasks
                .Where(task => !task.IsTerminal)
                .Select(task => (task, isInProgress: IsTaskInProgressForForcedTerminalization(task, isTaskTrackedAsRunning)))
                .OrderByDescending(entry => entry.isInProgress)
                .ThenByDescending(entry => GetTaskDepth(entry.task))
                .ToList();
            foreach ((ExecutionTask task, bool taskIsInProgress) in outstandingTasks)
            {
                /* Earlier parent completions can terminalize untouched descendants through propagation even though this
                   loop already snapshotted them as outstanding, so re-check terminality on each iteration. */
                if (task.IsTerminal)
                {
                    continue;
                }

                ExecutionTaskOutcome nextOutcome = taskIsInProgress
                    ? userCancelled ? ExecutionTaskOutcome.Cancelled : ExecutionTaskOutcome.Interrupted
                    : ExecutionTaskOutcome.Skipped;
                if (preserveRunningTerminalOutcomes && taskIsInProgress)
                {
                    CompleteTaskLifecycleCore(task.Id, nextOutcome);
                    continue;
                }

                CompleteTaskLifecycleCore(task.Id, nextOutcome);
            }
        });
    }

    /// <summary>
    /// Starts one scheduler-ready task through the single runtime start path. The session owns graph-level task
    /// resolution, runtime-context construction, and the visible Running transition. The scheduler remains responsible
    /// only for execution policy such as lock reservation, worker-thread dispatch, and completion orchestration, so it
    /// provides only the transport that actually invokes the already-bound body delegate.
    /// </summary>
    internal TaskStartResult StartTaskAsync(ExecutionTaskId taskId, Func<ExecutionTaskId, ILogger> createLogger, CancellationToken cancellationToken, ExecutionPlanScheduler scheduler, TaskExecutionRunner runTaskAsync)
    {
        if (createLogger == null)
        {
            throw new ArgumentNullException(nameof(createLogger));
        }

        if (runTaskAsync == null)
        {
            throw new ArgumentNullException(nameof(runTaskAsync));
        }

        ExecutionTask task = GetTask(taskId);
        ValidatedOperationParameters validatedOperationParameters = new(task.Title, task.OperationParameters, task.DeclaredOptionTypes);
        ExecutionTaskRuntimeServices runtime = new(this, scheduler, createLogger);
        ExecutionTaskContext context = new(task.Id, task.Title, createLogger(task.Id), cancellationToken, validatedOperationParameters, task.Operation, runtime);
        ExecutionTask startedTask = task.ResolveTaskToStart();
        ExecutionTask visibleStartedTask = task;
        ExecutionTaskContext startedContext = startedTask.Id == task.Id ? context : context.CreateForTask(startedTask);
        IReadOnlyList<ExecutionLock> executionLocks = startedTask.GetDeclaredExecutionLocks();
        ILogger taskLogger = createLogger(startedTask.Id);

        if (executionLocks.Count == 0)
        {
            /* Lock-free tasks can enter Running at admission time because there is no external resource gate between the
               scheduler's chosen start order and the body becoming eligible to execute. This preserves deterministic
               start-order observations while lock-waiting tasks still surface their explicit intermediate state. */
            startedContext.GetRequiredRuntime().SetTaskState(visibleStartedTask.Id, ExecutionTaskState.Running);
        }
        else
        {
            /* Lock-waiting must become visible immediately at admission time on the authored task that declared the lock.
               If this transition waits for the worker coroutine to begin, the authored task can still read as Queued or
               get rolled into Running through descendant-derived state before the explicit wait state is ever observed. */
            SetTaskWaitingForExecutionLock(visibleStartedTask.Id);
        }

        Func<Task<OperationResult>> executeAsync = async () =>
        {
            /* Once the scheduler admits this task as the chosen contender for its lock set, the task execution path owns
               the remaining lifecycle: enter explicit lock-wait state, acquire the declared locks, transition to Running,
               then execute the body under the held lock. */
            await using IAsyncDisposable acquiredLocks = await AcquireExecutionLocksForStartedTaskAsync(startedContext, startedTask, executionLocks, taskLogger, cancellationToken).ConfigureAwait(false);
            if (executionLocks.Count > 0)
            {
                startedContext.GetRequiredRuntime().SetTaskState(visibleStartedTask.Id, ExecutionTaskState.Running);
            }

            return await startedTask.Spec.ExecuteAsync!(startedContext).ConfigureAwait(false);
        };

        Task<OperationResult> runningTask = runTaskAsync(visibleStartedTask, executeAsync);
        return new TaskStartResult(visibleStartedTask, runningTask);
    }

    /// <summary>
    /// Acquires the declared execution locks for one already-admitted task. The session owns the visible transition into
    /// AwaitingLock, while the task's execution coroutine owns the actual wait and later Running transition.
    /// </summary>
    private void SetTaskWaitingForExecutionLock(ExecutionTaskId taskId)
    {
        SetTaskState(taskId, ExecutionTaskState.AwaitingLock);
    }

    /// <summary>
    /// Delegates the actual lock wait to task runtime services after the authored task has already published its visible
    /// AwaitingLock state at admission time.
    /// </summary>
    private Task<IAsyncDisposable> AcquireExecutionLocksForStartedTaskAsync(ExecutionTaskContext startedContext, ExecutionTask executingTask, IReadOnlyList<ExecutionLock> executionLocks, ILogger taskLogger, CancellationToken cancellationToken)
    {
        return startedContext.GetRequiredRuntime().AcquireExecutionLocksAsync(executingTask, executionLocks, taskLogger, cancellationToken);
    }

    /// <summary>
    /// Completes the root task when host-level shutdown needs to guarantee a terminal session outcome even if execution
    /// stopped before normal scheduler finalization reached the root.
    /// </summary>
    private void CompleteRootTaskIfNeeded(ExecutionTaskOutcome outcome)
    {
        if (_rootTask == null)
        {
            return;
        }

        ExecutionTask rootTask = RootTask;
        if (rootTask.State == ExecutionTaskState.Completed)
        {
            return;
        }

        CompleteTaskWithOutcome(rootTask.Id, outcome);
    }

    /// <summary>
    /// Forces the live graph into a coherent terminal state after an unexpected session-level exception. Started work is
    /// interrupted, untouched work is skipped, and the root preserves the failed session outcome.
    /// </summary>
    private void FailOutstandingTasksAfterFatalException()
    {
        if (_rootTask == null)
        {
            return;
        }

        /* Fatal session failure should propagate the same cancellation signal that ordinary interruption paths use so any
           cooperative running task can begin unwinding immediately instead of continuing after the host has already
           declared the session failed. */
        _cancellationTokenSource.Cancel();

        /* Mark the root as failed before terminalizing descendants so later ancestor-refresh completion preserves the
           failed session outcome instead of deriving success from the forced descendant completions. */
        if (RootTask.State != ExecutionTaskState.Completed)
        {
            SetTaskOutcome(RootTask.Id, ExecutionTaskOutcome.Failed);
        }

        /* Finalize descendants from the leaves upward so container refresh sees already-terminal children instead of
           bouncing parent state repeatedly while deeper active work is still being closed out. */
        List<ExecutionTask> outstandingTasks = Tasks
            .Where(task => task.Id != RootTask.Id && !task.IsTerminal)
            .OrderByDescending(GetTaskDepth)
            .ToList();
        foreach (ExecutionTask task in outstandingTasks)
        {
            if (IsTaskInProgressForForcedTerminalization(task))
            {
                CompleteTaskLifecycle(task.Id, ExecutionTaskOutcome.Interrupted);
                continue;
            }

            CompleteTaskWithOutcome(task.Id, ExecutionTaskOutcome.Skipped);
        }

        CompleteRootTaskIfNeeded(ExecutionTaskOutcome.Failed);
    }

    /// <summary>
    /// Returns the number of ancestors above one task so fatal-session cleanup can finalize descendants before their
    /// containing scopes.
    /// </summary>
    private static int GetTaskDepth(ExecutionTask task)
    {
        int depth = 0;
        ExecutionTask? currentTask = task.Parent;
        while (currentTask != null)
        {
            depth += 1;
            currentTask = currentTask.Parent;
        }

        return depth;
    }

    /// <summary>
    /// Marks the session as fully complete so host UI layers can await the authoritative runtime completion signal.
    /// </summary>
    private void CompleteExecution()
    {
        CleanupTempRoot();
        FinishedAt ??= DateTimeOffset.Now;
        _completionSource.TrySetResult(true);
    }

    /// <summary>
    /// Executes one delegate on a worker thread so UI-triggered runs remain asynchronous even though the live session now
    /// owns the top-level scheduler lifecycle directly.
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
    /// continue to work while the scheduler emits task-scoped output.
    /// </summary>
    private EventStreamLogger CreateAggregatingLogger()
    {
        return CreateAggregatingLogger(
            _sessionLogger,
            _sessionLogger,
            _sessionLogger,
            (level, output) =>
            {
                _sessionLogger.Log(level, output);
            });
    }

    /// <summary>
    /// Deletes the temp workspace owned by this session once the run has fully finished so session-scoped scratch data
    /// does not accumulate across completed runs.
    /// </summary>
    private void CleanupTempRoot()
    {
        string sessionTempRoot = OutputPaths.GetSessionTempRoot(Id);
        if (!Directory.Exists(sessionTempRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(sessionTempRoot, recursive: true);
            _sessionLogger.LogInformation("Deleted session temp root '{SessionTempRoot}'.", sessionTempRoot);
        }
        catch (Exception ex)
        {
            _sessionLogger.LogWarning(ex, "Failed to delete session temp root '{SessionTempRoot}'.", sessionTempRoot);
        }
    }

    /// <summary>
    /// Creates an event-stream logger that forwards formatted output to the supplied sink while preserving task logging
    /// and task-state routing when the host logger supports them.
    /// </summary>
    internal static EventStreamLogger CreateAggregatingLogger(ILogger fallbackLogger, IExecutionTaskLoggerFactory? taskLoggerFactory, IExecutionTaskStateSink? taskStateSink, Action<LogLevel, string>? onOutput)
    {
        if (fallbackLogger == null)
        {
            throw new ArgumentNullException(nameof(fallbackLogger));
        }

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
        /* Snapshot the root operation and metrics under the graph read lock, then emit the summary logs only after the
           lock is released. Session logging appends to buffered streams through the graph write path, so logging while a
           read lock is still held can recurse into an illegal write-under-read acquisition. */
        (Operation operation, int warningCount, int errorCount) snapshot = WithGraphReadLock(() =>
        {
            ExecutionTask rootTask = _rootTask ?? throw new InvalidOperationException("Session has no root task.");
            ExecutionTaskMetrics metrics = rootTask.GetSubtreeMetrics();
            return (rootTask.Operation, metrics.WarningCount, metrics.ErrorCount);
        });

        return FinalizeOutcome(snapshot.operation, result, logger, _sessionLogger, snapshot.warningCount, snapshot.errorCount);
    }

    /// <summary>
    /// Applies the historical operation-level warning and error rollup semantics for both top-level and nested runs.
    /// </summary>
    internal static OperationResult FinalizeOutcome(Operation operation, OperationResult result, ILogger aggregateLogger, ILogger summaryLogger, int warningCount, int errorCount)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (result == null)
        {
            throw new ArgumentNullException(nameof(result));
        }

        if (aggregateLogger == null)
        {
            throw new ArgumentNullException(nameof(aggregateLogger));
        }

        if (summaryLogger == null)
        {
            throw new ArgumentNullException(nameof(summaryLogger));
        }

        if (result.Outcome == ExecutionTaskOutcome.Cancelled)
        {
            aggregateLogger.LogWarning("Operation '{OperationName}' terminated by user", operation.OperationName);
        }
        else if (result.Outcome == ExecutionTaskOutcome.Interrupted)
        {
            aggregateLogger.LogWarning("Operation '{OperationName}' was interrupted - {ErrorCount} error(s), {WarningCount} warning(s)", operation.OperationName, errorCount, warningCount);
        }
        else if (result.Outcome == ExecutionTaskOutcome.Completed)
        {
            aggregateLogger.LogInformation("Operation '{OperationName}' completed successfully - {ErrorCount} error(s), {WarningCount} warning(s)", operation.OperationName, errorCount, warningCount);
        }
        else
        {
            aggregateLogger.LogWarning("Operation '{OperationName}' finished with failure - {ErrorCount} error(s), {WarningCount} warning(s)", operation.OperationName, errorCount, warningCount);
        }

        if (result.Outcome == ExecutionTaskOutcome.Completed)
        {
            if (errorCount > 0)
            {
                aggregateLogger.LogError("{ErrorCount} error(s) encountered", errorCount);
                result.Outcome = ExecutionTaskOutcome.Failed;
            }

            if (warningCount > 0)
            {
                aggregateLogger.LogWarning("{WarningCount} warning(s) encountered", warningCount);
                if (operation.ShouldFailOnWarning())
                {
                    aggregateLogger.LogError("Operation fails on warnings");
                    result.Outcome = ExecutionTaskOutcome.Failed;
                }
            }
        }

        if (result.Outcome == ExecutionTaskOutcome.Completed)
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
        else if (result.Outcome == ExecutionTaskOutcome.Cancelled)
        {
            summaryLogger.LogWarning("'{OperationName}' finished with result: cancelled", operation.OperationName);
        }
        else if (result.Outcome == ExecutionTaskOutcome.Interrupted)
        {
            summaryLogger.LogWarning("'{OperationName}' finished with result: interrupted", operation.OperationName);
        }
        else
        {
            summaryLogger.LogError("'{OperationName}' finished with result: failure", operation.OperationName);
        }

        return result;
    }

    /// <summary>
    /// Routes session and task log output into the session's buffered streams while still mirroring the same messages to
    /// the process-wide fallback logger.
    /// </summary>
    private sealed class SessionLogger : ILogger, IExecutionTaskLoggerFactory, IExecutionTaskStateSink, IExecutionTaskScope
    {
        private readonly ExecutionSession _session;
        private readonly ILogger _fallbackLogger;
        private readonly ExecutionTaskId? _taskId;

        /// <summary>
        /// Creates one session-scoped logger around the provided session and optional task id.
        /// </summary>
        public SessionLogger(ExecutionSession session, ExecutionTaskId? taskId = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _fallbackLogger = ResolveCurrentLogger();
            _taskId = taskId;
        }

        /// <summary>
        /// Gets the current task scope carried by this logger so nested operations can inherit the same task identity.
        /// </summary>
        public ExecutionTaskId? CurrentTaskId => _taskId;

        /// <summary>
        /// Indicates that all log levels are enabled for the buffered execution stream.
        /// </summary>
        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        /// <summary>
        /// Returns a fallback logger scope when available while keeping session log capture independent of structured
        /// scope state.
        /// </summary>
        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return _fallbackLogger.BeginScope(state) ?? NullScope.Instance;
        }

        /// <summary>
        /// Writes one formatted log message into the session and task streams, then mirrors the same output to the
        /// process-wide fallback logger.
        /// </summary>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            if (exception != null)
            {
                message += Environment.NewLine + exception;
            }

            _session.AddLogEntry(new LogEntry
            {
                SessionId = _session.Id.Value,
                TaskId = _taskId?.Value,
                Message = message,
                Verbosity = logLevel
            });

            _fallbackLogger.Log(logLevel, eventId, state, exception, formatter);
        }

        /// <summary>
        /// Creates a child logger that attributes all output to the provided execution task.
        /// </summary>
        public ILogger CreateTaskLogger(ExecutionTaskId taskId)
        {
            return new SessionLogger(_session, taskId);
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

        /// <summary>
        /// Forwards explicit task-state transitions into the session so graph views can react without parsing log text.
        /// </summary>
        public void SetTaskState(ExecutionTaskId taskId, ExecutionTaskState state)
        {
            _session.SetTaskState(taskId, state);
        }

        /// <summary>
        /// Provides a no-op scope object when the fallback logger does not create real scopes.
        /// </summary>
        private sealed class NullScope : IDisposable
        {
            /// <summary>
            /// Gets the shared no-op scope instance.
            /// </summary>
            public static NullScope Instance { get; } = new();

            /// <summary>
            /// Disposes the no-op scope.
            /// </summary>
            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// Returns the current live task definition for one task id. Walks the task tree from the root to find the matching
    /// node. O(n) per lookup is acceptable for execution trees of 10-100 nodes where simplicity outweighs index overhead.
    /// </summary>
    public ExecutionTask GetTask(ExecutionTaskId taskId)
    {
        return WithGraphReadLock(() => GetTaskCore(taskId));
    }

    /// <summary>
    /// Runs one task-graph mutation while holding the shared write lock. The lock scope must stay limited to graph
    /// structure and graph-derived state only; task execution and external waits stay outside this lock.
    /// </summary>
    private void WithGraphWriteLock(Action action)
    {
        _ = WithGraphWriteLock(() =>
        {
            action();
            return true;
        });
    }

    /// <summary>
    /// Runs one task-graph mutation while holding the shared write lock and returns its result.
    /// </summary>
    private T WithGraphWriteLock<T>(Func<T> action)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch waitStopwatch = Stopwatch.StartNew();
        _graphLock.EnterWriteLock();
        waitStopwatch.Stop();

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.GraphWriteLock")
            .SetTag("lock.wait_ms", waitStopwatch.Elapsed.TotalMilliseconds.ToString("0.###"));

        List<Action>? afterGraphWriteReleasedCallbacks = null;
        ExceptionDispatchInfo? capturedException = null;
        T result = default!;
        try
        {
            _graphWriteLockDepth += 1;
            result = action();
        }
        catch (Exception ex)
        {
            capturedException = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            /* Only the outermost write scope is allowed to detach the queued notifications for post-lock dispatch. */
            if (_graphWriteLockDepth == 1 && _afterGraphWriteReleasedCallbacks.Count > 0)
            {
                afterGraphWriteReleasedCallbacks = _afterGraphWriteReleasedCallbacks.ToList();
                _afterGraphWriteReleasedCallbacks.Clear();
            }

            _graphWriteLockDepth -= 1;
            TimeSpan holdDuration = totalStopwatch.Elapsed - waitStopwatch.Elapsed;
            activity.SetTag("lock.hold_ms", holdDuration.TotalMilliseconds.ToString("0.###"))
                .SetTag("lock.total_ms", totalStopwatch.Elapsed.TotalMilliseconds.ToString("0.###"));
            _graphLock.ExitWriteLock();
        }

        if (afterGraphWriteReleasedCallbacks != null)
        {
            try
            {
                ExecuteAfterGraphWriteReleasedCallbacks(afterGraphWriteReleasedCallbacks);
            }
            catch when (capturedException != null)
            {
                /* Preserve the original graph-mutation failure if both the mutation and a deferred subscriber fail. */
            }
        }

        capturedException?.Throw();
        return result;
    }

    /// <summary>
    /// Runs one task-graph read while holding the shared read lock so concurrent traversal stays coherent without
    /// blocking every other reader.
    /// </summary>
    private void WithGraphReadLock(Action action)
    {
        WithGraphLock("ExecutionSession.GraphReadLock", action, _graphLock.EnterReadLock, _graphLock.ExitReadLock);
    }

    /// <summary>
    /// Runs one task-graph read while holding the shared read lock and returns its result.
    /// </summary>
    private T WithGraphReadLock<T>(Func<T> action)
    {
        return WithGraphLock("ExecutionSession.GraphReadLock", action, _graphLock.EnterReadLock, _graphLock.ExitReadLock);
    }

    /// <summary>
    /// Measures how long one graph operation waited to acquire the session lock and how long that operation then held
    /// the lock while it executed.
    /// </summary>
    private static void WithGraphLock(string activityName, Action action, Action enterLock, Action exitLock)
    {
        WithGraphLock(
            activityName,
            () =>
            {
                action();
                return true;
            },
            enterLock,
            exitLock);
    }

    /// <summary>
    /// Measures graph-lock wait and hold time around one graph operation and returns the caller's result.
    /// </summary>
    private static T WithGraphLock<T>(string activityName, Func<T> action, Action enterLock, Action exitLock)
    {
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch waitStopwatch = Stopwatch.StartNew();
        enterLock();
        waitStopwatch.Stop();

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity(activityName)
            .SetTag("lock.wait_ms", waitStopwatch.Elapsed.TotalMilliseconds.ToString("0.###"));

        try
        {
            return action();
        }
        finally
        {
            TimeSpan holdDuration = totalStopwatch.Elapsed - waitStopwatch.Elapsed;
            activity.SetTag("lock.hold_ms", holdDuration.TotalMilliseconds.ToString("0.###"))
                .SetTag("lock.total_ms", totalStopwatch.Elapsed.TotalMilliseconds.ToString("0.###"));
            exitLock();
        }
    }

    /// <summary>
    /// Resolves one live task from the current root without taking the graph lock. Callers must already hold the graph
    /// lock when using this helper.
    /// </summary>
    private ExecutionTask GetTaskCore(ExecutionTaskId taskId)
    {
        ExecutionTask? task = _rootTask?.FindTask(taskId);
        if (task == null)
        {
            throw new InvalidOperationException($"Execution session does not contain task '{taskId}'.");
        }

        return task;
    }

    /// <summary>
    /// Builds a human-readable task path from the current live parent chain so logs can identify one task by its visible
    /// location in the execution tree instead of only by its generated id.
    /// </summary>
    public string GetTaskDisplayPath(ExecutionTaskId taskId)
    {
        return WithGraphReadLock(() => GetTaskCore(taskId).GetDisplayPath());
    }

    /// <summary>
    /// Returns whether the current runtime state for one task is terminal.
    /// </summary>
    public bool IsTaskTerminal(ExecutionTaskId taskId)
    {
        return WithGraphReadLock(() => GetTaskCore(taskId).IsTerminal);
    }

    private void SetTaskStateCore(ExecutionTaskId taskId, ExecutionTaskState state)
    {
        ExecutionTask task = GetTaskCore(taskId);
        if (task.TransitionState(state))
        {
            task.RecomputeSubtreeSchedulingRollup();
            RefreshTaskAndAncestorTimingMetrics(task);
        }
    }

    /// <summary>
    /// Records one semantic outcome change while the caller already holds the graph lock.
    /// </summary>
    private void SetTaskOutcomeCore(ExecutionTaskId taskId, ExecutionTaskOutcome? outcome)
    {
        ExecutionTask task = GetTaskCore(taskId);
        task.TransitionOutcome(outcome);
    }

    /// <summary>
    /// Interrupts one task while the caller already holds the graph lock.
    /// </summary>
    private void InterruptTaskCore(ExecutionTaskId taskId)
    {
        GetTaskCore(taskId).Interrupt();
        RefreshAncestorTaskStates(taskId);
    }

    /// <summary>
    /// Completes one task lifecycle while the caller already holds the graph lock.
    /// </summary>
    private void CompleteTaskLifecycleCore(ExecutionTaskId taskId, ExecutionTaskOutcome successOutcome = ExecutionTaskOutcome.Completed)
    {
        GetTaskCore(taskId).CompleteLifecycle(successOutcome);
        RefreshAncestorTaskStates(taskId);
    }

    /// <summary>
    /// Completes one task status update while the caller already holds the graph lock.
    /// </summary>
    private void SetTaskStatusCore(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        ExecutionTask task = GetTaskCore(taskId);
        ApplyTaskStatusCore(task, state, outcome, refreshTimingMetrics: true);
    }

    /// <summary>
    /// Applies one combined lifecycle/result status update to the supplied task. Callers may skip timing refresh when they are
    /// only updating an ancestor's rolled-up runtime state after timing has already been recomputed for the originating
    /// task change.
    /// </summary>
    private bool ApplyTaskStatusCore(ExecutionTask task, ExecutionTaskState state, ExecutionTaskOutcome? outcome, bool refreshTimingMetrics)
    {
        if (!task.TransitionStatus(state, outcome))
        {
            return false;
        }

        if (refreshTimingMetrics)
        {
            RefreshTaskAndAncestorTimingMetrics(task);
        }

        return true;
    }

    /// <summary>
    /// Re-emits one task-owned state change through the session-wide fanout so scheduler, UI, and any other session-level
    /// observers can subscribe once per session instead of wiring every task individually.
    /// </summary>
    private void HandleTaskStateChanged(ExecutionTask task, ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        Action<ExecutionTaskId, ExecutionTaskState, ExecutionTaskOutcome?>? taskStateChanged = TaskStateChanged;
        if (taskStateChanged == null)
        {
            return;
        }

        /* Capture the subscriber status payload at mutation time, then publish it only after the outermost
           graph write scope releases. Subscribers should observe the completed task-state change, but they must not become
           part of the graph-lock critical section. */
        AfterGraphWriteReleased(() =>
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.TaskStateChanged.InvokePostLock")
                .SetTag("task.id", task.Id.Value)
                .SetTag("task.state", state.ToString())
                .SetTag("task.outcome", outcome?.ToString() ?? string.Empty)
                .SetTag("subscriber.count", taskStateChanged.GetInvocationList().Length);
            taskStateChanged.Invoke(task.Id, state, outcome);
        });
    }

    /// <summary>
    /// Registers one callback that should run only after the outermost graph write scope releases the write lock.
    /// </summary>
    private void AfterGraphWriteReleased(Action callback)
    {
        if (callback == null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        /* If no write scope is active on this thread, the callback can run immediately because there is no deferred graph
           mutation boundary left to wait for. */
        if (!_graphLock.IsWriteLockHeld || _graphWriteLockDepth == 0)
        {
            callback();
            return;
        }

        _afterGraphWriteReleasedCallbacks.Add(callback);
    }

    /// <summary>
    /// Executes the deferred post-write callbacks after the graph write lock has been released.
    /// </summary>
    private static void ExecuteAfterGraphWriteReleasedCallbacks(IReadOnlyList<Action> afterGraphWriteReleasedCallbacks)
    {
        foreach (Action callback in afterGraphWriteReleasedCallbacks)
        {
            callback();
        }
    }

    /// <summary>
    /// Completes one task with one explicit semantic outcome while the caller already holds the graph lock.
    /// </summary>
    private void CompleteTaskWithOutcomeCore(ExecutionTaskId taskId, ExecutionTaskOutcome outcome)
    {
        GetTaskCore(taskId).CompleteWithOutcome(outcome);
        RefreshAncestorTaskStates(taskId);
    }

    /// <summary>
    /// Records the semantic outcome for one task without changing its current lifecycle status. This lets a scope become
    /// doomed while nested work is still unwinding.
    /// </summary>
    internal void SetTaskOutcome(ExecutionTaskId taskId, ExecutionTaskOutcome? outcome)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.SetTaskOutcome")
            .SetTag("task.id", taskId.Value)
            .SetTag("task.outcome", outcome?.ToString() ?? string.Empty);
        WithGraphWriteLock(() => SetTaskOutcomeCore(taskId, outcome));
    }

    /// <summary>
    /// Marks one task as semantically interrupted without forcing its lifecycle to completed, then refreshes ancestor
    /// container state. Running work can keep unwinding, while untouched queued work should use the skipped completion
    /// path instead.
    /// </summary>
    internal void InterruptTask(ExecutionTaskId taskId)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.InterruptTask")
            .SetTag("task.id", taskId.Value);
        WithGraphWriteLock(() => InterruptTaskCore(taskId));
    }

    /// <summary>
    /// Completes one task lifecycle while preserving any previously assigned doomed outcome, then refreshes ancestor
    /// container state. The task resolves its own final outcome while the session handles cross-branch ancestor refresh.
    /// </summary>
    internal void CompleteTaskLifecycle(ExecutionTaskId taskId, ExecutionTaskOutcome successOutcome = ExecutionTaskOutcome.Completed)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.CompleteTaskLifecycle")
            .SetTag("task.id", taskId.Value)
            .SetTag("task.outcome", successOutcome.ToString());
        WithGraphWriteLock(() => CompleteTaskLifecycleCore(taskId, successOutcome));
    }

    /// <summary>
    /// Applies one lifecycle/result update as a single externally visible state change so observers never see torn state
    /// between status and semantic result fields.
    /// </summary>
    private void SetTaskStatus(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.SetTaskStatus")
            .SetTag("task.id", taskId.Value)
            .SetTag("task.state", state.ToString())
            .SetTag("task.outcome", outcome?.ToString() ?? string.Empty);
        WithGraphWriteLock(() => SetTaskStatusCore(taskId, state, outcome));
    }

    /// <summary>
    /// Completes one task lifecycle and assigns its semantic outcome, propagating to untouched descendants and then
    /// refreshing ancestor container state. The task owns per-subtree work while this session method handles the
    /// cross-branch ancestor refresh that follows.
    /// </summary>
    internal void CompleteTaskWithOutcome(ExecutionTaskId taskId, ExecutionTaskOutcome outcome)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.CompleteTaskWithOutcome")
            .SetTag("task.id", taskId.Value)
            .SetTag("task.outcome", outcome.ToString());
        WithGraphWriteLock(() => CompleteTaskWithOutcomeCore(taskId, outcome));
    }

    private TimeSpan GetSessionDuration(DateTimeOffset? now)
    {
        DateTimeOffset endTime = FinishedAt ?? now ?? DateTimeOffset.Now;
        TimeSpan duration = endTime - StartedAt;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    /// <summary>
    /// Converts one log entry into the warning/error delta that subtree and session metrics should record.
    /// </summary>
    private static (int warningDelta, int errorDelta) GetLogCountDeltas(LogEntry entry)
    {
        int warningDelta = entry.Verbosity == LogLevel.Warning ? 1 : 0;
        int errorDelta = entry.Verbosity >= LogLevel.Error ? 1 : 0;
        return (warningDelta, errorDelta);
    }

    /// <summary>
    /// Applies one warning/error delta to the emitting task and every ancestor that includes that task in its subtree.
    /// </summary>
    private static void ApplyTaskLogMetrics(ExecutionTask task, int warningDelta, int errorDelta)
    {
        if (warningDelta == 0 && errorDelta == 0)
        {
            return;
        }

        for (ExecutionTask? currentTask = task; currentTask != null; currentTask = currentTask.Parent)
        {
            currentTask.ApplySubtreeLogDelta(warningDelta, errorDelta);
        }
    }

    /// <summary>
    /// Recomputes cached subtree timing metrics for one changed task and then for every ancestor above it so later reads
    /// can materialize subtree duration without walking the full descendant tree again.
    /// </summary>
    private static void RefreshTaskAndAncestorTimingMetrics(ExecutionTask task)
    {
        for (ExecutionTask? currentTask = task; currentTask != null; currentTask = currentTask.Parent)
        {
            currentTask.RecomputeSubtreeTimingMetrics();
        }
    }

    /// <summary>
    /// Propagates task-owned subtree rollups upward through ancestor scopes and refreshes only the ancestors whose
    /// rolled-up runtime state or cached scheduler summary actually changed.
    /// </summary>
    private void RefreshAncestorTaskStates(ExecutionTaskId taskId)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.RefreshAncestorTaskStates")
            .SetTag("task.id", taskId.Value);
        int ancestorCount = 0;
        int totalChildCount = 0;
        int changedStatusCount = 0;
        int changedRollupCount = 0;
        ExecutionTask task = GetTask(taskId);
        task.RecomputeSubtreeSchedulingRollup();
        ExecutionTask? currentParent = task.Parent;
        while (currentParent != null)
        {
            ancestorCount += 1;
            IReadOnlyList<ExecutionTask> childTasks = currentParent.Children;
            totalChildCount += childTasks.Count;
            if (childTasks.Count == 0)
            {
                currentParent = currentParent.Parent;
                continue;
            }

            bool rollupChanged = currentParent.RecomputeSubtreeSchedulingRollup();
            (ExecutionTaskState parentState, ExecutionTaskOutcome? parentOutcome) = currentParent.ComputeRolledUpStateFromChildren();
            bool statusChanged = ApplyTaskStatusCore(currentParent, parentState, parentOutcome, refreshTimingMetrics: false);
            bool postStatusRollupChanged = statusChanged && currentParent.RecomputeSubtreeSchedulingRollup();
            if (statusChanged)
            {
                changedStatusCount += 1;
            }

            if (rollupChanged || postStatusRollupChanged)
            {
                changedRollupCount += 1;
            }

            if (!statusChanged && !rollupChanged)
            {
                break;
            }

            currentParent = currentParent.Parent;
        }

        activity.SetTag("ancestor.count", ancestorCount)
            .SetTag("child.count", totalChildCount)
            .SetTag("changed.status.count", changedStatusCount)
            .SetTag("changed.rollup.count", changedRollupCount);
    }

    /// <summary>
    /// Seeds the live runtime graph from the initial authored plan. The cloned task instances then become the
    /// authoritative home for all per-task runtime data, while the session keeps only graph/session coordination state.
    /// </summary>
    private void InitializeFromPlan(ExecutionPlan plan)
    {
        /* Authored task specs already contain dependency IDs, so a single clone pass is sufficient to seed the live
           session graph. */
        foreach (ExecutionTask task in plan.Tasks)
        {
            AddTask(task.CloneForSession());
        }

        if (_rootTask != null)
        {
            RebuildSubtreeSchedulingRollups(_rootTask.GetAllTasks());
        }
    }

    /// <summary>
    /// Registers one task in the live session graph, wires parent-child object references, and initializes the task-owned
    /// runtime state that will stay attached to that task for the rest of the session. Dependency IDs are already part of
    /// each cloned task's authored spec, so only parent-child object references need wiring here.
    /// </summary>
    private void AddTask(ExecutionTask task)
    {
        WithGraphWriteLock(() => AddTaskCore(task));
    }

    /// <summary>
    /// Attaches one task to the live session graph while the caller already holds the graph lock.
    /// </summary>
    private void AddTaskCore(ExecutionTask task)
    {
        /* Duplicate detection walks the tree instead of checking a dictionary index. This is only called during plan
           initialization and child-task merging, not on the scheduler hot path. */
        if (_rootTask?.FindTask(task.Id) != null)
        {
            throw new InvalidOperationException($"Execution session already contains task '{task.Id}'.");
        }

        task.InitializeRuntimeState();
        task.StateChanged += HandleTaskStateChanged;

        if (task.ParentId is ExecutionTaskId parentId)
        {
            /* Wire the parent-child object reference. GetTask walks the existing tree to find the parent, then
               AddChild sets the child's _parent back-reference and adds it to the parent's _children list. */
            GetTask(parentId).AddChild(task);
        }
        else
        {
            /* First task without a parent becomes the session root. Sessions always have exactly one root task. */
            if (_rootTask != null)
            {
                throw new InvalidOperationException($"Execution session already has a root task '{_rootTask.Id}'. Cannot add second root '{task.Id}'.");
            }

            _rootTask = task;
        }

    }

    /// <summary>
    /// Rebuilds cached scheduler rollups for a fully attached subtree from the leaves upward so each parent observes
    /// already-updated child summaries instead of a partially wired intermediate tree.
    /// </summary>
    private static void RebuildSubtreeSchedulingRollups(IEnumerable<ExecutionTask> tasks)
    {
        foreach (ExecutionTask task in tasks.OrderByDescending(GetTaskDepth))
        {
            task.RecomputeSubtreeSchedulingRollup();
        }
    }
}

/// <summary>
/// Describes the live-graph changes produced when one task adds nested child tasks at runtime.
/// </summary>
public sealed class ChildTaskMergeResult
{
    public ChildTaskMergeResult(ExecutionTask rootTask, IReadOnlyList<ExecutionTask> descendants, IReadOnlyList<ExecutionTaskId> insertedTaskIds)
    {
        RootTask = rootTask ?? throw new ArgumentNullException(nameof(rootTask));
        Descendants = descendants ?? throw new ArgumentNullException(nameof(descendants));
        InsertedTaskIds = insertedTaskIds ?? throw new ArgumentNullException(nameof(insertedTaskIds));
    }

    public ExecutionTask RootTask { get; }

    public IReadOnlyList<ExecutionTask> Descendants { get; }

    public IReadOnlyList<ExecutionTaskId> InsertedTaskIds { get; }
}
