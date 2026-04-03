using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents one live execution session. The session owns the current task graph and session lifecycle, while each
/// execution task owns its own mutable runtime state, graph links, and task-scoped log stream.
/// </summary>
public sealed class ExecutionSession
{
    private readonly Func<Task>? _cancelAsync;
    private readonly TaskCompletionSource<bool> _completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ExecutionTask> _tasks = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionTask> _tasksById = new();
    private bool _hasBegunExecution;

    /// <summary>
    /// Creates an execution session around a shared log stream and optional cancellation action.
    /// </summary>
    public ExecutionSession(ILogStream logStream, Func<Task>? cancelAsync = null, ExecutionPlan? plan = null)
    {
        Id = ExecutionSessionId.New();
        LogStream = logStream ?? throw new ArgumentNullException(nameof(logStream));
        StartedAt = DateTimeOffset.Now;
        _cancelAsync = cancelAsync;

        if (plan != null)
        {
            InitializeFromPlan(plan);
        }
    }

    public event Action<ExecutionTaskId, ExecutionTaskState, ExecutionTaskOutcome?, string?>? TaskStateChanged;
    public event Action? TaskGraphChanged;

    public ExecutionSessionId Id { get; }

    public ILogStream LogStream { get; }

    /// <summary>
    /// Gets the live session-owned task graph. Sessions clone plan tasks when they start so runtime mutations never
    /// modify preview plan objects.
    /// </summary>
    public IReadOnlyList<ExecutionTask> Tasks => new ReadOnlyCollection<ExecutionTask>(_tasks);

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets the task that completes when the runtime has fully finalized the session outcome and running state.
    /// </summary>
    public Task Completion => _completionSource.Task;

    public ExecutionTaskOutcome? Outcome { get; set; }

    public bool? Success
    {
        get => Outcome == null ? null : Outcome == ExecutionTaskOutcome.Completed;
        set => Outcome = value == null ? null : (value.Value ? ExecutionTaskOutcome.Completed : ExecutionTaskOutcome.Failed);
    }

    public string OperationName { get; set; } = string.Empty;

    public string TargetName { get; set; } = string.Empty;

    public IReadOnlyList<ExecutionTaskId> GetTaskSubtreeIds(ExecutionTaskId taskId)
    {
        List<ExecutionTaskId> subtreeIds = new() { taskId };
        CollectDescendantTaskIds(taskId, subtreeIds);
        return subtreeIds;
    }

    public ExecutionTaskMetrics GetTaskMetrics(ExecutionTaskId? taskId, DateTimeOffset? now = null)
    {
        if (taskId == null)
        {
            return new ExecutionTaskMetrics(GetSessionDuration(now), CountWarnings(LogStream.Entries), CountErrors(LogStream.Entries));
        }

        IReadOnlyList<ExecutionTaskId> subtreeIds = GetTaskSubtreeIds(taskId.Value);
        HashSet<ExecutionTaskId> subtreeIdSet = new(subtreeIds);
        IReadOnlyList<LogEntry> subtreeEntries = LogStream.Entries
            .Where(entry => ExecutionTaskId.FromNullable(entry.TaskId) is ExecutionTaskId entryTaskId && subtreeIdSet.Contains(entryTaskId))
            .ToList();

        return new ExecutionTaskMetrics(
            GetTaskDuration(taskId, now),
            CountWarnings(subtreeEntries),
            CountErrors(subtreeEntries));
    }

    public TimeSpan? GetTaskDuration(ExecutionTaskId? taskId, DateTimeOffset? now = null)
    {
        if (taskId == null)
        {
            return null;
        }

        IReadOnlyList<ExecutionTaskId> subtreeIds = GetTaskSubtreeIds(taskId.Value);
        DateTimeOffset? effectiveStart = null;
        DateTimeOffset? effectiveEnd = null;
        DateTimeOffset timestampNow = now ?? DateTimeOffset.Now;

        foreach (ExecutionTaskId subtreeTaskId in subtreeIds)
        {
            ExecutionTask task = GetTask(subtreeTaskId);
            if (task.StartedAt != null)
            {
                effectiveStart = effectiveStart == null || task.StartedAt < effectiveStart ? task.StartedAt : effectiveStart;
            }

            if (task.State == ExecutionTaskState.Running)
            {
                effectiveEnd = timestampNow;
                continue;
            }

            if (task.FinishedAt != null && (effectiveEnd == null || task.FinishedAt > effectiveEnd))
            {
                effectiveEnd = task.FinishedAt;
            }
        }

        if (effectiveStart == null)
        {
            return null;
        }

        DateTimeOffset resolvedEnd = effectiveEnd ?? timestampNow;
        TimeSpan duration = resolvedEnd - effectiveStart.Value;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    public void AddLogEntry(LogEntry entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        LogStream.Add(entry);
        ExecutionTaskId? taskId = ExecutionTaskId.FromNullable(entry.TaskId);
        if (taskId == null)
        {
            return;
        }

        GetTask(taskId.Value).LogStream.Add(entry);
    }

    public BufferedLogStream? GetTaskLogStream(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return _tasksById.TryGetValue(taskId.Value, out ExecutionTask? task) ? task.LogStream : null;
    }

    public void SetTaskState(ExecutionTaskId taskId, ExecutionTaskState state, string? statusReason = null)
    {
        /* Public state writes update execution state only. Semantic outcome is derived separately so callbacks,
           containers, and child-failure propagation can share one runtime model without overloading one enum with two
           meanings. */
        SetTaskStateCore(taskId, state, statusReason);
        RefreshAncestorTaskStates(taskId);
    }

    /// <summary>
    /// Fails one task scope, skips untouched descendants, interrupts started sibling subtrees, and propagates the failure
    /// up through ancestors so scope status follows hierarchy rather than only explicit dependency edges.
    /// </summary>
    public void FailScopeFromTask(ExecutionTaskId failedTaskId, string? statusReason = null)
    {
        /* A failed subtree keeps lifecycle and semantic outcome separate: the failing task completes with Result=Failed,
           untouched descendants complete with Result=Skipped, and sibling subtrees that already began real work are
           marked Interrupted while still allowing active callbacks to unwind. */
        CompleteTaskWithOutcome(failedTaskId, ExecutionTaskOutcome.Failed, statusReason);
        SkipUnfinishedDescendants(failedTaskId, BuildInheritedDescendantReason(failedTaskId, ExecutionTaskOutcome.Failed, statusReason));

        ExecutionTaskId failedSiblingRootId = failedTaskId;
        ExecutionTaskId? currentAncestorId = GetTask(failedTaskId).ParentId;
        while (currentAncestorId != null)
        {
            ExecutionTask ancestorTask = GetTask(currentAncestorId.Value);
            foreach (ExecutionTaskId childTaskId in ancestorTask.ChildTaskIds)
            {
                if (childTaskId == failedSiblingRootId)
                {
                    continue;
                }

                InterruptSiblingSubtree(childTaskId, failedSiblingRootId);
            }

            CompleteTaskWithOutcome(currentAncestorId.Value, ExecutionTaskOutcome.Failed, statusReason);
            failedSiblingRootId = currentAncestorId.Value;
            currentAncestorId = ancestorTask.ParentId;
        }
    }

    public void BeginExecution()
    {
        _hasBegunExecution = true;
        foreach (ExecutionTask task in _tasks.Where(task => task.State == ExecutionTaskState.Planned))
        {
            if (!task.Enabled)
            {
                task.State = ExecutionTaskState.Completed;
                task.StatusReason = task.DisabledReason;
                continue;
            }

            task.State = ExecutionTaskState.Pending;
            task.StatusReason = string.Empty;
        }
    }

    [Obsolete("Use task.State directly instead.")]
    public ExecutionTaskState? GetTaskState(ExecutionTaskId? taskId)
    {
        return taskId == null ? null : GetTask(taskId.Value).State;
    }

    [Obsolete("Use task.StatusReason directly instead.")]
    public string? GetTaskStatusReason(ExecutionTaskId? taskId)
    {
        return taskId == null ? null : GetTask(taskId.Value).StatusReason;
    }

    /// <summary>
    /// Inserts one child plan beneath the currently executing task in the live session graph using the shared
    /// task-insertion path.
    /// </summary>
    public ChildTaskMergeResult MergeChildTasks(ExecutionTaskId parentTaskId, ExecutionPlan childPlan)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionSession.MergeChildTasks")
            .SetTag("parent.task.id", parentTaskId.Value)
            .SetTag("child.plan.title", childPlan.Title)
            .SetTag("child.plan.task.count", childPlan.Tasks.Count);

        _ = GetTask(parentTaskId);
        InsertedExecutionTasks insertedTasks;
        using (PerformanceActivityScope insertActivity = PerformanceTelemetry.StartActivity("ExecutionSession.MergeChildTasks.InsertUnderParent"))
        {
            insertedTasks = ExecutionTaskInsertion.InsertUnderParent(childPlan, parentTaskId, ExecutionTaskId.New);
            insertActivity.SetTag("inserted.task.count", insertedTasks.Tasks.Count)
                .SetTag("inserted.root.id", insertedTasks.RootTaskId.Value);
        }

        List<ExecutionTaskId> insertedTaskIds = new();
        using (PerformanceActivityScope addTasksActivity = PerformanceTelemetry.StartActivity("ExecutionSession.MergeChildTasks.AddTasks"))
        {
            foreach (ExecutionTask insertedTask in insertedTasks.Tasks)
            {
                AddTask(insertedTask);
                insertedTaskIds.Add(insertedTask.Id);
            }

            addTasksActivity.SetTag("inserted.task.count", insertedTaskIds.Count);
        }

        TaskGraphChanged?.Invoke();
        ChildTaskMergeResult mergeResult = new(GetTask(insertedTasks.RootTaskId), insertedTasks.Tasks, insertedTaskIds);
        activity.SetTag("inserted.root.title", mergeResult.RootTask.Title)
            .SetTag("inserted.task.count", mergeResult.InsertedTaskIds.Count);
        return mergeResult;
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

        void OnTaskStateChanged(ExecutionTaskId taskId, ExecutionTaskState _, ExecutionTaskOutcome? __, string? ___)
        {
            if (!IsTaskTerminal(taskId))
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
        lock (remainingTaskIds)
        {
            foreach (ExecutionTaskId taskId in remainingTaskIds.ToList())
            {
                if (IsTaskTerminal(taskId))
                {
                    CompleteIfFinished(taskId);
                }
            }
        }

        return completionSource.Task;
    }

    public Task CancelAsync()
    {
        return _cancelAsync != null ? _cancelAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// Marks the session as fully complete so host UI layers can await the authoritative runtime completion signal.
    /// </summary>
    public void CompleteExecution()
    {
        _completionSource.TrySetResult(true);
    }

    /// <summary>
    /// Returns the current live task definition for one task id.
    /// </summary>
    public ExecutionTask GetTask(ExecutionTaskId taskId)
    {
        if (!_tasksById.TryGetValue(taskId, out ExecutionTask? task))
        {
            throw new InvalidOperationException($"Execution session does not contain task '{taskId}'.");
        }

        return task;
    }

    /// <summary>
    /// Returns the current explicit dependency ids for one task within the live session graph.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> GetTaskDependencies(ExecutionTaskId taskId)
    {
        return GetTask(taskId).DependsOn;
    }

    /// <summary>
    /// Builds a human-readable task path from the current live parent chain so logs can identify one task by its visible
    /// location in the execution tree instead of only by its generated id.
    /// </summary>
    public string GetTaskDisplayPath(ExecutionTaskId taskId)
    {
        List<string> segments = new();
        ExecutionTask? currentTask = GetTask(taskId);
        while (currentTask != null)
        {
            segments.Add(currentTask.Title);
            currentTask = currentTask.ParentId is ExecutionTaskId parentId
                ? GetTask(parentId)
                : null;
        }

        segments.Reverse();
        return string.Join(" > ", segments);
    }

    /// <summary>
    /// Returns whether the current runtime state for one task is terminal.
    /// </summary>
    public bool IsTaskTerminal(ExecutionTaskId taskId)
    {
        return GetTask(taskId).State == ExecutionTaskState.Completed;
    }

    private void SetTaskStateCore(ExecutionTaskId taskId, ExecutionTaskState state, string? statusReason)
    {
        ExecutionTask task = GetTask(taskId);
        string normalizedReason = statusReason ?? string.Empty;
        if (task.State == state && string.Equals(task.StatusReason, normalizedReason, StringComparison.Ordinal))
        {
            return;
        }

        ValidateStateTransition(task, state);
        ValidateObservedState(task.Id, state, task.Outcome);
        (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) = ResolveTaskTiming(task, state);
        task.ApplyRuntimeState(state, normalizedReason, task.Outcome, startedAt, finishedAt);
        TaskStateChanged?.Invoke(taskId, state, task.Outcome, statusReason);
    }

    private static (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) ResolveTaskTiming(ExecutionTask task, ExecutionTaskState state)
    {
        DateTimeOffset? startedAt = task.StartedAt;
        DateTimeOffset? finishedAt = task.FinishedAt;
        DateTimeOffset timestamp = DateTimeOffset.Now;

        if (state == ExecutionTaskState.Running && task.StartedAt == null)
        {
            startedAt = timestamp;
            finishedAt = null;
            return (startedAt, finishedAt);
        }

        if (state == ExecutionTaskState.Completed)
        {
            startedAt ??= timestamp;
            finishedAt = timestamp;
        }

        return (startedAt, finishedAt);
    }

    /// <summary>
    /// Records the semantic outcome for one task without changing its current lifecycle status. This lets a scope become
    /// doomed while nested work is still unwinding.
    /// </summary>
    internal void SetTaskOutcome(ExecutionTaskId taskId, ExecutionTaskOutcome? outcome, string? statusReason = null)
    {
        ExecutionTask task = GetTask(taskId);
        string normalizedReason = statusReason ?? string.Empty;
        if (task.Outcome == outcome && (statusReason == null || string.Equals(task.StatusReason, normalizedReason, StringComparison.Ordinal)))
        {
            return;
        }

        ValidateOutcomeTransition(task, outcome);
        string effectiveReason = statusReason != null ? normalizedReason : task.StatusReason;
        ValidateObservedState(task.Id, task.State, outcome);
        task.ApplyRuntimeState(task.State, effectiveReason, outcome, task.StartedAt, task.FinishedAt);

        TaskStateChanged?.Invoke(taskId, task.State, task.Outcome, task.StatusReason);
    }

    /// <summary>
    /// Marks one task as semantically interrupted without forcing its lifecycle to completed. Running work can keep
    /// unwinding, while untouched queued work should use the skipped completion path instead.
    /// </summary>
    internal void InterruptTask(ExecutionTaskId taskId, string? statusReason = null)
    {
        ExecutionTask task = GetTask(taskId);
        if (task.State == ExecutionTaskState.Completed)
        {
            throw new InvalidOperationException($"Task '{taskId}' cannot be interrupted after it already completed.");
        }

        if (!HasTaskStarted(task))
        {
            throw new InvalidOperationException($"Task '{taskId}' cannot be interrupted before execution has started.");
        }

        SetTaskOutcome(taskId, ExecutionTaskOutcome.Interrupted, statusReason);
        RefreshAncestorTaskStates(taskId);
    }

    /// <summary>
    /// Completes one task lifecycle while preserving any previously assigned failure, cancellation, or interruption result
    /// that was recorded while descendant work was still unwinding.
    /// </summary>
    internal void CompleteTaskLifecycle(ExecutionTaskId taskId, ExecutionTaskOutcome successOutcome = ExecutionTaskOutcome.Completed, string? statusReason = null)
    {
        ExecutionTask task = GetTask(taskId);
        ExecutionTaskOutcome finalOutcome = task.Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted
            ? task.Outcome.Value
            : successOutcome;
        string? finalReason = finalOutcome == successOutcome ? statusReason : task.StatusReason;
        CompleteTaskWithOutcome(taskId, finalOutcome, finalReason);
    }

    /// <summary>
    /// Applies one lifecycle/result update as a single externally visible state change so observers never see torn state
    /// between status and semantic result fields.
    /// </summary>
    private void SetTaskSnapshot(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome, string? statusReason)
    {
        ExecutionTask task = GetTask(taskId);
        string normalizedReason = statusReason ?? string.Empty;
        if (task.State == state && task.Outcome == outcome && string.Equals(task.StatusReason, normalizedReason, StringComparison.Ordinal))
        {
            return;
        }

        ValidateStateTransition(task, state);
        ValidateOutcomeTransition(task, outcome);
        ValidateObservedState(task.Id, state, outcome);
        (DateTimeOffset? startedAt, DateTimeOffset? finishedAt) = ResolveTaskTiming(task, state);
        task.ApplyRuntimeState(state, normalizedReason, outcome, startedAt, finishedAt);
        TaskStateChanged?.Invoke(taskId, state, outcome, statusReason);
    }

    /// <summary>
    /// Completes one task lifecycle and assigns its semantic outcome in a single operation so container rollups and UI
    /// projections observe a consistent state.
    /// </summary>
    internal void CompleteTaskWithOutcome(ExecutionTaskId taskId, ExecutionTaskOutcome outcome, string? statusReason = null)
    {
        ValidateTerminalAssignment(taskId, outcome);
        SetTaskSnapshot(taskId, ExecutionTaskState.Completed, outcome, statusReason);
        PropagateTerminalOutcomeToUntouchedDescendants(taskId, outcome, statusReason);
        RefreshAncestorTaskStates(taskId);
    }

    private void CollectDescendantTaskIds(ExecutionTaskId parentTaskId, ICollection<ExecutionTaskId> descendantTaskIds)
    {
        foreach (ExecutionTaskId childTaskId in GetTask(parentTaskId).ChildTaskIds)
        {
            descendantTaskIds.Add(childTaskId);
            CollectDescendantTaskIds(childTaskId, descendantTaskIds);
        }
    }

    private TimeSpan GetSessionDuration(DateTimeOffset? now)
    {
        DateTimeOffset endTime = FinishedAt ?? now ?? DateTimeOffset.Now;
        TimeSpan duration = endTime - StartedAt;
        return duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
    }

    private static int CountWarnings(IEnumerable<LogEntry> entries)
    {
        return entries.Count(entry => entry.Verbosity == LogLevel.Warning);
    }

    private static int CountErrors(IEnumerable<LogEntry> entries)
    {
        return entries.Count(entry => entry.Verbosity >= LogLevel.Error);
    }

    private void PropagateTerminalOutcomeToUntouchedDescendants(ExecutionTaskId parentTaskId, ExecutionTaskOutcome outcome, string? statusReason)
    {
        /* Descendants that never started inherit a completed lifecycle plus the semantic outcome that explains why they
           will never run. */
        ExecutionTaskOutcome descendantOutcome = outcome switch
        {
            ExecutionTaskOutcome.Skipped => ExecutionTaskOutcome.Skipped,
            ExecutionTaskOutcome.Cancelled => ExecutionTaskOutcome.Skipped,
            ExecutionTaskOutcome.Interrupted => ExecutionTaskOutcome.Skipped,
            ExecutionTaskOutcome.Failed => ExecutionTaskOutcome.Skipped,
            _ => default
        };

        if (descendantOutcome == default)
        {
            return;
        }

        string? descendantReason = BuildInheritedDescendantReason(parentTaskId, outcome, statusReason);
        SkipUnfinishedDescendants(parentTaskId, descendantReason, descendantOutcome);
    }

    /// <summary>
    /// Derives lifecycle and semantic outcome for container scopes from their children. Callback tasks own their own
    /// lifecycle directly, while authored/container tasks are finished by child aggregation only.
    /// </summary>
    private void RefreshAncestorTaskStates(ExecutionTaskId taskId)
    {
        ExecutionTaskId? currentParentId = GetTask(taskId).ParentId;
        while (currentParentId != null)
        {
            ExecutionTask parentTask = GetTask(currentParentId.Value);
            if (parentTask.ChildTaskIds.Count == 0 || parentTask.ExecuteAsync != null)
            {
                currentParentId = parentTask.ParentId;
                continue;
            }

            IReadOnlyList<ExecutionTask> childTasks = parentTask.ChildTaskIds
                .Select(GetTask)
                .ToList();

            bool anyChildRunning = childTasks.Any(task => task.State == ExecutionTaskState.Running);
            bool anyPending = childTasks.Any(task => task.State is ExecutionTaskState.Pending or ExecutionTaskState.Planned);
            ExecutionTask? failedTask = childTasks.FirstOrDefault(task => task.Outcome == ExecutionTaskOutcome.Failed);
            ExecutionTask? cancelledTask = childTasks.FirstOrDefault(task => task.Outcome == ExecutionTaskOutcome.Cancelled);
            ExecutionTask? interruptedTask = childTasks.FirstOrDefault(task => task.Outcome == ExecutionTaskOutcome.Interrupted);
            ExecutionTask? skippedTask = childTasks.FirstOrDefault(task => task.Outcome == ExecutionTaskOutcome.Skipped);
            bool allDisabled = childTasks.All(task => task.Outcome == ExecutionTaskOutcome.Disabled);

            ExecutionTaskState parentState;
            if (parentTask.Outcome is ExecutionTaskOutcome.Failed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Skipped)
            {
                parentState = ExecutionTaskState.Completed;
            }
            else if (anyChildRunning)
            {
                parentState = ExecutionTaskState.Running;
            }
            else if (anyPending)
            {
                /* Parent scope activation is scheduler-owned. Pending children should not make a parent appear Running on
                   their own, but an already-activated scope remains Running while it waits for those children to start. */
                parentState = parentTask.State == ExecutionTaskState.Running
                    ? ExecutionTaskState.Running
                    : ExecutionTaskState.Pending;
            }
            else
            {
                parentState = ExecutionTaskState.Completed;
            }

            ExecutionTaskOutcome? parentOutcome = anyPending || anyChildRunning
                ? failedTask != null
                    ? ExecutionTaskOutcome.Failed
                    : cancelledTask != null
                        ? ExecutionTaskOutcome.Cancelled
                        : interruptedTask != null
                            ? ExecutionTaskOutcome.Interrupted
                        : null
                : failedTask != null
                    ? ExecutionTaskOutcome.Failed
                    : cancelledTask != null
                        ? ExecutionTaskOutcome.Cancelled
                        : interruptedTask != null
                            ? ExecutionTaskOutcome.Interrupted
                        : allDisabled
                            ? ExecutionTaskOutcome.Disabled
                            : skippedTask != null
                                ? ExecutionTaskOutcome.Skipped
                                : ExecutionTaskOutcome.Completed;
            string? parentReason = failedTask?.StatusReason;
            parentReason ??= cancelledTask?.StatusReason;
            parentReason ??= interruptedTask?.StatusReason;
            parentReason ??= skippedTask?.StatusReason;
            if (allDisabled)
            {
                parentReason ??= "All child tasks are disabled.";
            }

            if (parentOutcome == ExecutionTaskOutcome.Completed && childTasks.Any(childTask => childTask.State != ExecutionTaskState.Completed))
            {
                throw new InvalidOperationException($"Task '{currentParentId.Value}' cannot roll up to completed while child work is still non-terminal.");
            }

            SetTaskSnapshot(currentParentId.Value, parentState, parentOutcome, parentReason);
            currentParentId = parentTask.ParentId;
        }
    }

    /// <summary>
    /// Skips every unfinished descendant beneath the provided parent task.
    /// </summary>
    private void SkipUnfinishedDescendants(ExecutionTaskId parentTaskId, string? reason, ExecutionTaskOutcome skippedOutcome = ExecutionTaskOutcome.Skipped)
    {
        foreach (ExecutionTaskId childTaskId in GetTask(parentTaskId).ChildTaskIds)
        {
            SkipUnfinishedSubtree(childTaskId, reason, skippedOutcome);
        }
    }

    /// <summary>
     /// Skips every unfinished task in the provided subtree without disturbing tasks that already reached terminal states.
     /// </summary>
    private void SkipUnfinishedSubtree(ExecutionTaskId rootTaskId, string? reason, ExecutionTaskOutcome skippedOutcome = ExecutionTaskOutcome.Skipped)
    {
        ExecutionTask task = GetTask(rootTaskId);
        if (task.State is ExecutionTaskState.Planned or ExecutionTaskState.Pending)
        {
            CompleteTaskWithOutcome(rootTaskId, skippedOutcome, reason);
        }

        foreach (ExecutionTaskId childTaskId in task.ChildTaskIds)
        {
            SkipUnfinishedSubtree(childTaskId, reason, skippedOutcome);
        }
    }

    /// <summary>
    /// Interrupts one sibling subtree after a different sibling has failed. Subtrees that already started work surface as
    /// Interrupted, while untouched subtrees remain Skipped.
    /// </summary>
    private void InterruptSiblingSubtree(ExecutionTaskId rootTaskId, ExecutionTaskId failedSiblingRootId)
    {
        bool subtreeStarted = HasStartedWorkInSubtree(rootTaskId);
        string reason = subtreeStarted
            ? $"Interrupted because sibling task '{failedSiblingRootId}' failed."
            : $"Skipped because sibling task '{failedSiblingRootId}' failed.";

        if (subtreeStarted)
        {
            MarkSubtreeInterrupted(rootTaskId, reason);
            return;
        }

        SkipUnfinishedSubtree(rootTaskId, reason, ExecutionTaskOutcome.Skipped);
    }

    /// <summary>
    /// Returns whether any task in the subtree has already transitioned out of its untouched planned or pending state.
    /// </summary>
    private bool HasStartedWorkInSubtree(ExecutionTaskId rootTaskId)
    {
        ExecutionTask task = GetTask(rootTaskId);
        if (HasTaskStarted(task))
        {
            return true;
        }

        return task.ChildTaskIds.Any(HasStartedWorkInSubtree);
    }

    /// <summary>
    /// Returns whether any descendant beneath the provided task is still pending or running.
    /// </summary>
    private bool HasNonTerminalDescendants(ExecutionTaskId rootTaskId)
    {
        return GetTask(rootTaskId).ChildTaskIds.Any(childTaskId =>
        {
            ExecutionTask childTask = GetTask(childTaskId);
            return childTask.State != ExecutionTaskState.Completed || HasNonTerminalDescendants(childTaskId);
        });
    }

    /// <summary>
    /// Marks one started subtree as interrupted after sibling failure. Started nodes keep their current lifecycle so any
    /// active callbacks can finish unwinding, while untouched nodes become skipped because they never ran.
    /// </summary>
    private void MarkSubtreeInterrupted(ExecutionTaskId rootTaskId, string? reason)
    {
        ExecutionTask task = GetTask(rootTaskId);
        if (task.State != ExecutionTaskState.Completed)
        {
            if (HasTaskStarted(task))
            {
                InterruptTask(rootTaskId, reason);
            }
            else
            {
                CompleteTaskWithOutcome(rootTaskId, ExecutionTaskOutcome.Skipped, reason);
            }
        }

        foreach (ExecutionTaskId childTaskId in task.ChildTaskIds)
        {
            MarkSubtreeInterrupted(childTaskId, reason);
        }
    }

    /// <summary>
    /// Returns whether a single task has entered real execution instead of remaining untouched queued work.
    /// </summary>
    private static bool HasTaskStarted(ExecutionTask task)
    {
        if (task.State == ExecutionTaskState.Running || task.StartedAt != null)
        {
            return true;
        }

        return task.Outcome is ExecutionTaskOutcome.Completed or ExecutionTaskOutcome.Cancelled or ExecutionTaskOutcome.Interrupted or ExecutionTaskOutcome.Failed;
    }

    /// <summary>
    /// Guards lifecycle transitions so task state only moves forward through the execution model.
    /// </summary>
    private static void ValidateStateTransition(ExecutionTask task, ExecutionTaskState nextState)
    {
        if (task.State == ExecutionTaskState.Completed && nextState != ExecutionTaskState.Completed)
        {
            throw new InvalidOperationException($"Task '{task.Id}' cannot transition from completed execution state back to '{nextState}'.");
        }

        if (task.State == ExecutionTaskState.Running && nextState is ExecutionTaskState.Pending or ExecutionTaskState.Planned)
        {
            throw new InvalidOperationException($"Task '{task.Id}' cannot transition from running to '{nextState}'.");
        }
    }

    /// <summary>
    /// Guards combined observable state so lifecycle and semantic outcome never describe contradictory execution states.
    /// </summary>
    private static void ValidateObservedState(ExecutionTaskId taskId, ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        if (state == ExecutionTaskState.Running && outcome is ExecutionTaskOutcome.Completed or ExecutionTaskOutcome.Skipped or ExecutionTaskOutcome.Disabled)
        {
            throw new InvalidOperationException($"Task '{taskId}' cannot be running while reporting semantic outcome '{outcome}'.");
        }

        if (state is ExecutionTaskState.Planned or ExecutionTaskState.Pending && outcome != null)
        {
            throw new InvalidOperationException($"Task '{taskId}' cannot remain queued while reporting semantic outcome '{outcome}'.");
        }

        if (state == ExecutionTaskState.Completed && outcome == null)
        {
            throw new InvalidOperationException($"Task '{taskId}' cannot complete its execution state without a semantic outcome.");
        }
    }

    /// <summary>
    /// Guards semantic result transitions so only stricter outcomes can replace a previously assigned result.
    /// </summary>
    private static void ValidateOutcomeTransition(ExecutionTask task, ExecutionTaskOutcome? nextOutcome)
    {
        if (task.Outcome == null || nextOutcome == null || task.Outcome == nextOutcome)
        {
            return;
        }

        if (CanUpgradeTaskOutcome(task.Outcome.Value, nextOutcome.Value))
        {
            return;
        }

        throw new InvalidOperationException($"Task '{task.Id}' cannot change outcome from '{task.Outcome}' to '{nextOutcome}'.");
    }

    /// <summary>
    /// Defines the small set of semantic result upgrades that remain legal after a task has already been marked with a
    /// weaker doomed outcome.
    /// </summary>
    private static bool CanUpgradeTaskOutcome(ExecutionTaskOutcome currentOutcome, ExecutionTaskOutcome nextOutcome)
    {
        return (currentOutcome, nextOutcome) switch
        {
            (ExecutionTaskOutcome.Interrupted, ExecutionTaskOutcome.Failed) => true,
            (ExecutionTaskOutcome.Cancelled, ExecutionTaskOutcome.Failed) => true,
            (ExecutionTaskOutcome.Interrupted, ExecutionTaskOutcome.Cancelled) => true,
            _ => false
        };
    }

    /// <summary>
    /// Guards terminal assignments so untouched-only outcomes cannot be applied after a task or subtree has started real
    /// execution.
    /// </summary>
    private void ValidateTerminalAssignment(ExecutionTaskId taskId, ExecutionTaskOutcome outcome)
    {
        if (outcome == ExecutionTaskOutcome.Completed && HasNonTerminalDescendants(taskId))
        {
            throw new InvalidOperationException($"Task '{taskId}' cannot complete successfully while it still has non-terminal descendants.");
        }

        if (outcome == ExecutionTaskOutcome.Skipped && HasStartedWorkInSubtree(taskId))
        {
            throw new InvalidOperationException($"Task '{taskId}' cannot be marked skipped after execution has started in its subtree.");
        }

        if (outcome == ExecutionTaskOutcome.Interrupted && !HasStartedWorkInSubtree(taskId))
        {
            throw new InvalidOperationException($"Task '{taskId}' cannot be marked interrupted before execution has started in its subtree.");
        }
    }

    private string? BuildInheritedDescendantReason(ExecutionTaskId parentTaskId, ExecutionTaskOutcome outcome, string? statusReason)
    {
        if (!string.IsNullOrWhiteSpace(statusReason))
        {
            return outcome == ExecutionTaskOutcome.Skipped
                ? statusReason
                : $"Skipped because parent task '{parentTaskId}' {outcome.ToString().ToLowerInvariant()}: {statusReason}";
        }

        return outcome switch
        {
            ExecutionTaskOutcome.Skipped => $"Skipped because parent task '{parentTaskId}' was skipped.",
            ExecutionTaskOutcome.Cancelled => $"Skipped because parent task '{parentTaskId}' was cancelled.",
            ExecutionTaskOutcome.Interrupted => $"Skipped because parent task '{parentTaskId}' was interrupted.",
            ExecutionTaskOutcome.Failed => $"Skipped because parent task '{parentTaskId}' failed.",
            _ => null
        };
    }

    /// <summary>
    /// Seeds the session-owned runtime graph from the initial authored plan and then treats the cloned session task
    /// instances as the authoritative runtime source of truth from that point forward.
    /// </summary>
    private void InitializeFromPlan(ExecutionPlan plan)
    {
        foreach (ExecutionTask task in plan.Tasks)
        {
            AddTask(task.CloneForSession());
        }
    }

    /// <summary>
    /// Registers one task in the live session graph and initializes its task-owned runtime state.
    /// </summary>
    private void AddTask(ExecutionTask task)
    {
        if (!_tasksById.TryAdd(task.Id, task))
        {
            throw new InvalidOperationException($"Execution session already contains task '{task.Id}'.");
        }

        _tasks.Add(task);
        task.InitializeRuntimeState(_hasBegunExecution);

        if (task.ParentId is ExecutionTaskId parentId)
        {
            GetTask(parentId).AddChild(task.Id);
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
