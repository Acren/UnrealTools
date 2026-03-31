using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents a running or recently completed execution owned by the runtime layer.
/// </summary>
public sealed class ExecutionSession
{
    private readonly Func<Task>? _cancelAsync;
    private readonly Dictionary<ExecutionTaskId, BufferedLogStream> _taskLogStreams = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionTaskRuntimeState> _taskRuntimeStates = new();
    private readonly Dictionary<ExecutionTaskId, List<ExecutionTaskId>> _childTaskIdsByParent = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionTaskId?> _parentTaskIds = new();

    /// <summary>
    /// Creates an execution session around a shared log stream and optional cancellation action.
    /// </summary>
    public ExecutionSession(ILogStream logStream, Func<Task>? cancelAsync = null, ExecutionPlan? plan = null)
    {
        Id = ExecutionSessionId.New();
        LogStream = logStream ?? throw new ArgumentNullException(nameof(logStream));
        StartedAt = DateTimeOffset.Now;
        _cancelAsync = cancelAsync;
        Plan = plan;

        if (plan != null)
        {
            foreach (ExecutionPlanTask task in plan.Tasks)
            {
                _taskLogStreams[task.Id] = new BufferedLogStream();
                ExecutionTaskStatus initialStatus = task.Enabled ? ExecutionTaskStatus.Planned : ExecutionTaskStatus.Disabled;
                string initialReason = task.Enabled ? string.Empty : task.DisabledReason;
                _taskRuntimeStates[task.Id] = new ExecutionTaskRuntimeState(task.Id, initialStatus, initialReason);
                _parentTaskIds[task.Id] = task.ParentId;

                if (task.ParentId is ExecutionTaskId parentId)
                {
                    if (!_childTaskIdsByParent.TryGetValue(parentId, out List<ExecutionTaskId>? childTaskIds))
                    {
                        childTaskIds = new List<ExecutionTaskId>();
                        _childTaskIdsByParent[parentId] = childTaskIds;
                    }

                    childTaskIds.Add(task.Id);
                }
            }
        }
    }

    public event Action<ExecutionTaskId, ExecutionTaskStatus, string?>? TaskStatusChanged;

    public ExecutionSessionId Id { get; }

    public ExecutionPlan? Plan { get; }

    public ILogStream LogStream { get; }

    public IReadOnlyDictionary<ExecutionTaskId, BufferedLogStream> TaskLogStreams => new ReadOnlyDictionary<ExecutionTaskId, BufferedLogStream>(_taskLogStreams);

    [Obsolete("Use TaskRuntimeStates or GetTaskRuntimeState instead.")]
    public IReadOnlyDictionary<ExecutionTaskId, ExecutionTaskStatus> TaskStatuses => new ReadOnlyDictionary<ExecutionTaskId, ExecutionTaskStatus>(
        _taskRuntimeStates.ToDictionary(pair => pair.Key, pair => pair.Value.Status));

    public IReadOnlyDictionary<ExecutionTaskId, ExecutionTaskRuntimeState> TaskRuntimeStates => new ReadOnlyDictionary<ExecutionTaskId, ExecutionTaskRuntimeState>(_taskRuntimeStates);

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? FinishedAt { get; set; }

    public bool IsRunning { get; set; }

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
            if (_taskRuntimeStates.TryGetValue(subtreeTaskId, out ExecutionTaskRuntimeState? runtimeState) && runtimeState.StartedAt != null)
            {
                effectiveStart = effectiveStart == null || runtimeState.StartedAt < effectiveStart ? runtimeState.StartedAt : effectiveStart;
            }

            if (_taskRuntimeStates.TryGetValue(subtreeTaskId, out runtimeState) && runtimeState.Status == ExecutionTaskStatus.Running)
            {
                effectiveEnd = timestampNow;
                continue;
            }

            if (_taskRuntimeStates.TryGetValue(subtreeTaskId, out runtimeState) && runtimeState.FinishedAt != null)
            {
                if (effectiveEnd == null || runtimeState.FinishedAt > effectiveEnd)
                {
                    effectiveEnd = runtimeState.FinishedAt;
                }
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

        EnsureTaskLogStream(taskId.Value).Add(entry);
    }

    public BufferedLogStream EnsureTaskLogStream(ExecutionTaskId taskId)
    {
        if (_taskLogStreams.TryGetValue(taskId, out BufferedLogStream? existingStream))
        {
            return existingStream;
        }

        BufferedLogStream createdStream = new();
        _taskLogStreams[taskId] = createdStream;
        return createdStream;
    }

    public BufferedLogStream? GetTaskLogStream(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return _taskLogStreams.TryGetValue(taskId.Value, out BufferedLogStream? logStream) ? logStream : null;
    }

    public void SetTaskStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null)
    {
        SetTaskStatusCore(taskId, status, statusReason);
        PropagateTerminalStatusToUntouchedDescendants(taskId, status, statusReason);
        RefreshAncestorTaskStatuses(taskId);
    }

    public void BeginExecution()
    {
        foreach (ExecutionTaskId taskId in _taskRuntimeStates.Keys.ToList())
        {
            if (_taskRuntimeStates[taskId].Status != ExecutionTaskStatus.Planned)
            {
                continue;
            }

            ExecutionTaskRuntimeState runtimeState = _taskRuntimeStates[taskId];
            runtimeState.Status = ExecutionTaskStatus.Pending;
            runtimeState.StatusReason = string.Empty;
        }
    }

    [Obsolete("Use GetTaskRuntimeState instead.")]
    public ExecutionTaskStatus? GetTaskStatus(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return _taskRuntimeStates.TryGetValue(taskId.Value, out ExecutionTaskRuntimeState? state) ? state.Status : null;
    }

    [Obsolete("Use GetTaskRuntimeState instead.")]
    public string? GetTaskStatusReason(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return _taskRuntimeStates.TryGetValue(taskId.Value, out ExecutionTaskRuntimeState? state) ? state.StatusReason : null;
    }

    public ExecutionTaskRuntimeState? GetTaskRuntimeState(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return _taskRuntimeStates.TryGetValue(taskId.Value, out ExecutionTaskRuntimeState? state) ? state : null;
    }

    public Task CancelAsync()
    {
        return _cancelAsync != null ? _cancelAsync() : Task.CompletedTask;
    }

    private void SetTaskStatusCore(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason)
    {
        RecordTaskTiming(taskId, status);
        ExecutionTaskRuntimeState runtimeState = EnsureTaskRuntimeState(taskId);
        runtimeState.Status = status;
        runtimeState.StatusReason = statusReason ?? string.Empty;
        TaskStatusChanged?.Invoke(taskId, status, statusReason);
    }

    private void RecordTaskTiming(ExecutionTaskId taskId, ExecutionTaskStatus status)
    {
        DateTimeOffset timestamp = DateTimeOffset.Now;
        ExecutionTaskRuntimeState runtimeState = EnsureTaskRuntimeState(taskId);

        if (status == ExecutionTaskStatus.Running && runtimeState.StartedAt == null)
        {
            runtimeState.StartedAt = timestamp;
            runtimeState.FinishedAt = null;
            return;
        }

        if (status is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Skipped or ExecutionTaskStatus.Disabled)
        {
            runtimeState.StartedAt ??= timestamp;
            runtimeState.FinishedAt = timestamp;
        }
    }

    private void CollectDescendantTaskIds(ExecutionTaskId parentTaskId, ICollection<ExecutionTaskId> descendantTaskIds)
    {
        if (!_childTaskIdsByParent.TryGetValue(parentTaskId, out List<ExecutionTaskId>? childTaskIds))
        {
            return;
        }

        foreach (ExecutionTaskId childTaskId in childTaskIds)
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
        if (!_childTaskIdsByParent.TryGetValue(parentTaskId, out List<ExecutionTaskId>? childTaskIds))
        {
            return;
        }

        ExecutionTaskStatus descendantStatus = status switch
        {
            ExecutionTaskStatus.Skipped => ExecutionTaskStatus.Skipped,
            ExecutionTaskStatus.Cancelled => ExecutionTaskStatus.Skipped,
            ExecutionTaskStatus.Failed => ExecutionTaskStatus.Skipped,
            _ => default
        };

        if (descendantStatus == default)
        {
            return;
        }

        string? descendantReason = BuildInheritedDescendantReason(parentTaskId, status, statusReason);
        foreach (ExecutionTaskId childTaskId in childTaskIds)
        {
            ExecutionTaskStatus childStatus = _taskRuntimeStates.TryGetValue(childTaskId, out ExecutionTaskRuntimeState? existingState)
                ? existingState.Status
                : ExecutionTaskStatus.Planned;
            if (childStatus is not ExecutionTaskStatus.Planned and not ExecutionTaskStatus.Pending)
            {
                continue;
            }

            SetTaskStatusCore(childTaskId, descendantStatus, descendantReason);
            PropagateTerminalStatusToUntouchedDescendants(childTaskId, descendantStatus, descendantReason);
        }
    }

    /// <summary>
    /// Derives parent-group runtime status from the current direct child statuses so non-executable hierarchy nodes stay
    /// aligned with the real state of their subtree.
    /// </summary>
    private void RefreshAncestorTaskStatuses(ExecutionTaskId taskId)
    {
        ExecutionTaskId? currentParentId = _parentTaskIds.TryGetValue(taskId, out ExecutionTaskId? parentId) ? parentId : null;
        while (currentParentId != null)
        {
            if (!_childTaskIdsByParent.TryGetValue(currentParentId.Value, out List<ExecutionTaskId>? childTaskIds) || childTaskIds.Count == 0)
            {
                currentParentId = _parentTaskIds.TryGetValue(currentParentId.Value, out ExecutionTaskId? ancestorParentIdForEmptyNode) ? ancestorParentIdForEmptyNode : null;
                continue;
            }

            IReadOnlyList<ExecutionTaskRuntimeState> childStates = childTaskIds
                .Select(childTaskId => EnsureTaskRuntimeState(childTaskId))
                .ToList();

            ExecutionTaskStatus parentStatus;
            string? parentReason = null;
            if (childStates.Any(state => state.Status == ExecutionTaskStatus.Running))
            {
                parentStatus = ExecutionTaskStatus.Running;
            }
            else if (childStates.Any(state => state.Status == ExecutionTaskStatus.Failed))
            {
                ExecutionTaskRuntimeState failedState = childStates.First(state => state.Status == ExecutionTaskStatus.Failed);
                parentStatus = ExecutionTaskStatus.Failed;
                parentReason = string.IsNullOrWhiteSpace(failedState.StatusReason)
                    ? "One or more child tasks failed."
                    : failedState.StatusReason;
            }
            else if (childStates.Any(state => state.Status == ExecutionTaskStatus.Cancelled))
            {
                parentStatus = ExecutionTaskStatus.Cancelled;
                parentReason = "One or more child tasks were cancelled.";
            }
            else if (childStates.Any(state => state.Status == ExecutionTaskStatus.Pending || state.Status == ExecutionTaskStatus.Planned))
            {
                parentStatus = ExecutionTaskStatus.Pending;
            }
            else if (childStates.All(state => state.Status == ExecutionTaskStatus.Disabled))
            {
                parentStatus = ExecutionTaskStatus.Disabled;
                parentReason = "All child tasks are disabled.";
            }
            else if (childStates.Any(state => state.Status == ExecutionTaskStatus.Skipped))
            {
                parentStatus = ExecutionTaskStatus.Skipped;
                parentReason = childStates.First(state => state.Status == ExecutionTaskStatus.Skipped).StatusReason;
            }
            else
            {
                parentStatus = ExecutionTaskStatus.Completed;
            }

            SetTaskStatusCore(currentParentId.Value, parentStatus, parentReason);
            currentParentId = _parentTaskIds.TryGetValue(currentParentId.Value, out ExecutionTaskId? ancestorParentIdForTraversal) ? ancestorParentIdForTraversal : null;
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

    private ExecutionTaskRuntimeState EnsureTaskRuntimeState(ExecutionTaskId taskId)
    {
        if (_taskRuntimeStates.TryGetValue(taskId, out ExecutionTaskRuntimeState? state))
        {
            return state;
        }

        state = new ExecutionTaskRuntimeState(taskId, ExecutionTaskStatus.Pending);
        _taskRuntimeStates[taskId] = state;
        return state;
    }
}
