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

    public event Action<ExecutionTaskId, ExecutionTaskStatus, string?>? TaskStatusChanged;
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

    public RunOutcome? Outcome { get; set; }

    public bool? Success
    {
        get => Outcome == null ? null : Outcome == RunOutcome.Succeeded;
        set => Outcome = value == null ? null : (value.Value ? RunOutcome.Succeeded : RunOutcome.Failed);
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

            if (task.Status == ExecutionTaskStatus.Running)
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

    public void SetTaskStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null)
    {
        /* Public status writes update lifecycle only. Semantic result is derived separately so callbacks, containers, and
           child-failure propagation can share the same scheduler model without overloading one enum with two meanings. */
        SetTaskStatusCore(taskId, status, statusReason);
        PropagateTerminalStatusToUntouchedDescendants(taskId, status, statusReason);
        RefreshAncestorTaskStates(taskId);
    }

    /// <summary>
    /// Fails one task scope, skips unfinished descendants and sibling branches recursively, and propagates the failure up
    /// through ancestors so scope status follows hierarchy rather than only explicit dependency edges.
    /// </summary>
    public void FailScopeFromTask(ExecutionTaskId failedTaskId, string? statusReason = null)
    {
        /* A failed subtree keeps lifecycle and semantic outcome separate: the failing task completes with Result=Failed,
           untouched descendants complete with Result=Skipped, and every ancestor scope inherits the failed result while its
           remaining sibling branches are completed as skipped. */
        CompleteTaskWithResult(failedTaskId, ExecutionTaskStatus.Failed, statusReason);
        SkipUnfinishedDescendants(failedTaskId, BuildInheritedDescendantReason(failedTaskId, ExecutionTaskStatus.Failed, statusReason));

        ExecutionTaskId failedBranchRootId = failedTaskId;
        ExecutionTaskId? currentAncestorId = GetTask(failedTaskId).ParentId;
        while (currentAncestorId != null)
        {
            ExecutionTask ancestorTask = GetTask(currentAncestorId.Value);
            foreach (ExecutionTaskId childTaskId in ancestorTask.ChildTaskIds)
            {
                if (childTaskId == failedBranchRootId)
                {
                    continue;
                }

                SkipUnfinishedSubtree(childTaskId, $"Skipped because sibling task '{failedBranchRootId}' failed.");
            }

            CompleteTaskWithResult(currentAncestorId.Value, ExecutionTaskStatus.Failed, statusReason);
            failedBranchRootId = currentAncestorId.Value;
            currentAncestorId = ancestorTask.ParentId;
        }
    }

    public void BeginExecution()
    {
        _hasBegunExecution = true;
        foreach (ExecutionTask task in _tasks.Where(task => task.Status == ExecutionTaskStatus.Planned))
        {
            if (!task.Enabled)
            {
                task.Status = ExecutionTaskStatus.Completed;
                task.StatusReason = task.DisabledReason;
                continue;
            }

            task.Status = ExecutionTaskStatus.Pending;
            task.StatusReason = string.Empty;
        }
    }

    [Obsolete("Use task.Status directly instead.")]
    public ExecutionTaskStatus? GetTaskStatus(ExecutionTaskId? taskId)
    {
        return taskId == null ? null : GetTask(taskId.Value).Status;
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

            TaskStatusChanged -= OnTaskStatusChanged;
            cancellationRegistration.Dispose();
            completionSource.TrySetResult(true);
        }

        void OnTaskStatusChanged(ExecutionTaskId taskId, ExecutionTaskStatus _, string? __)
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
            TaskStatusChanged -= OnTaskStatusChanged;
            completionSource.TrySetCanceled(cancellationToken);
        });

        TaskStatusChanged += OnTaskStatusChanged;
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
    /// Returns whether the current runtime state for one task is terminal.
    /// </summary>
    public bool IsTaskTerminal(ExecutionTaskId taskId)
    {
        return GetTask(taskId).Status == ExecutionTaskStatus.Completed;
    }

    private void SetTaskStatusCore(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason)
    {
        ExecutionTask task = GetTask(taskId);
        string normalizedReason = statusReason ?? string.Empty;
        if (task.Status == status && string.Equals(task.StatusReason, normalizedReason, StringComparison.Ordinal))
        {
            return;
        }

        RecordTaskTiming(task, status);
        task.Status = status;
        task.StatusReason = normalizedReason;
        TaskStatusChanged?.Invoke(taskId, status, statusReason);
    }

    private void RecordTaskTiming(ExecutionTask task, ExecutionTaskStatus status)
    {
        DateTimeOffset timestamp = DateTimeOffset.Now;

        if (status == ExecutionTaskStatus.Running && task.StartedAt == null)
        {
            task.StartedAt = timestamp;
            task.FinishedAt = null;
            return;
        }

        if (status == ExecutionTaskStatus.Completed)
        {
            task.StartedAt ??= timestamp;
            task.FinishedAt = timestamp;
        }
    }

    /// <summary>
    /// Records the semantic outcome for one task without changing its current lifecycle status. This lets a scope become
    /// doomed while nested work is still unwinding.
    /// </summary>
    internal void SetTaskResult(ExecutionTaskId taskId, ExecutionTaskStatus? result, string? statusReason = null)
    {
        ExecutionTask task = GetTask(taskId);
        string normalizedReason = statusReason ?? string.Empty;
        if (task.Result == result && (statusReason == null || string.Equals(task.StatusReason, normalizedReason, StringComparison.Ordinal)))
        {
            return;
        }

        task.Result = result;
        if (statusReason != null)
        {
            task.StatusReason = normalizedReason;
        }

        TaskStatusChanged?.Invoke(taskId, task.Status, task.StatusReason);
    }

    /// <summary>
    /// Completes one task lifecycle and assigns its semantic outcome in a single operation so container rollups and UI
    /// projections observe a consistent state.
    /// </summary>
    internal void CompleteTaskWithResult(ExecutionTaskId taskId, ExecutionTaskStatus result, string? statusReason = null)
    {
        SetTaskStatusCore(taskId, ExecutionTaskStatus.Completed, statusReason);
        SetTaskResult(taskId, result, statusReason);
        PropagateTerminalStatusToUntouchedDescendants(taskId, result, statusReason);
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

    private void PropagateTerminalStatusToUntouchedDescendants(ExecutionTaskId parentTaskId, ExecutionTaskStatus status, string? statusReason)
    {
        /* Descendants that never started inherit a completed lifecycle plus the semantic outcome that explains why they
           will never run. */
        ExecutionTaskStatus descendantResult = status switch
        {
            ExecutionTaskStatus.Skipped => ExecutionTaskStatus.Skipped,
            ExecutionTaskStatus.Cancelled => ExecutionTaskStatus.Skipped,
            ExecutionTaskStatus.Failed => ExecutionTaskStatus.Skipped,
            _ => default
        };

        if (descendantResult == default)
        {
            return;
        }

        string? descendantReason = BuildInheritedDescendantReason(parentTaskId, status, statusReason);
        SkipUnfinishedDescendants(parentTaskId, descendantReason, descendantResult);
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

            bool anyChildRunning = childTasks.Any(task => task.Status == ExecutionTaskStatus.Running);
            bool anyPending = childTasks.Any(task => task.Status is ExecutionTaskStatus.Pending or ExecutionTaskStatus.Planned);
            ExecutionTask? failedTask = childTasks.FirstOrDefault(task => task.Result == ExecutionTaskStatus.Failed);
            ExecutionTask? cancelledTask = childTasks.FirstOrDefault(task => task.Result == ExecutionTaskStatus.Cancelled);
            ExecutionTask? skippedTask = childTasks.FirstOrDefault(task => task.Result == ExecutionTaskStatus.Skipped);
            bool allDisabled = childTasks.All(task => task.Result == ExecutionTaskStatus.Disabled);

            ExecutionTaskStatus parentLifecycle;
            if (parentTask.Result is ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Skipped)
            {
                parentLifecycle = ExecutionTaskStatus.Completed;
            }
            else if (anyChildRunning)
            {
                parentLifecycle = ExecutionTaskStatus.Running;
            }
            else if (anyPending)
            {
                /* Parent scope activation is scheduler-owned. Pending children should not make a parent appear Running on
                   their own, but an already-activated scope remains Running while it waits for those children to start. */
                parentLifecycle = parentTask.Status == ExecutionTaskStatus.Running
                    ? ExecutionTaskStatus.Running
                    : ExecutionTaskStatus.Pending;
            }
            else
            {
                parentLifecycle = ExecutionTaskStatus.Completed;
            }

            ExecutionTaskStatus? parentResult = anyPending || anyChildRunning
                ? failedTask != null
                    ? ExecutionTaskStatus.Failed
                    : cancelledTask != null
                        ? ExecutionTaskStatus.Cancelled
                        : null
                : failedTask != null
                    ? ExecutionTaskStatus.Failed
                    : cancelledTask != null
                        ? ExecutionTaskStatus.Cancelled
                        : allDisabled
                            ? ExecutionTaskStatus.Disabled
                            : skippedTask != null
                                ? ExecutionTaskStatus.Skipped
                                : ExecutionTaskStatus.Completed;
            string? parentReason = failedTask?.StatusReason;
            parentReason ??= cancelledTask?.StatusReason;
            parentReason ??= skippedTask?.StatusReason;
            if (allDisabled)
            {
                parentReason ??= "All child tasks are disabled.";
            }

            SetTaskStatusCore(currentParentId.Value, parentLifecycle, parentReason);
            SetTaskResult(currentParentId.Value, parentResult, parentReason);
            currentParentId = parentTask.ParentId;
        }
    }

    /// <summary>
    /// Skips every unfinished descendant beneath the provided parent task.
    /// </summary>
    private void SkipUnfinishedDescendants(ExecutionTaskId parentTaskId, string? reason, ExecutionTaskStatus skippedResult = ExecutionTaskStatus.Skipped)
    {
        foreach (ExecutionTaskId childTaskId in GetTask(parentTaskId).ChildTaskIds)
        {
            SkipUnfinishedSubtree(childTaskId, reason, skippedResult);
        }
    }

    /// <summary>
    /// Skips every unfinished task in the provided subtree without disturbing tasks that already reached terminal states.
    /// </summary>
    private void SkipUnfinishedSubtree(ExecutionTaskId rootTaskId, string? reason, ExecutionTaskStatus skippedResult = ExecutionTaskStatus.Skipped)
    {
        ExecutionTask task = GetTask(rootTaskId);
        if (task.Status is ExecutionTaskStatus.Planned or ExecutionTaskStatus.Pending)
        {
            CompleteTaskWithResult(rootTaskId, skippedResult, reason);
        }

        foreach (ExecutionTaskId childTaskId in task.ChildTaskIds)
        {
            SkipUnfinishedSubtree(childTaskId, reason, skippedResult);
        }
    }

    private string? BuildInheritedDescendantReason(ExecutionTaskId parentTaskId, ExecutionTaskStatus status, string? statusReason)
    {
        if (!string.IsNullOrWhiteSpace(statusReason))
        {
            return status == ExecutionTaskStatus.Skipped
                ? statusReason
                : $"Skipped because parent task '{parentTaskId}' {status.ToString().ToLowerInvariant()}: {statusReason}";
        }

        return status switch
        {
            ExecutionTaskStatus.Skipped => $"Skipped because parent task '{parentTaskId}' was skipped.",
            ExecutionTaskStatus.Cancelled => $"Skipped because parent task '{parentTaskId}' was cancelled.",
            ExecutionTaskStatus.Failed => $"Skipped because parent task '{parentTaskId}' failed.",
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
