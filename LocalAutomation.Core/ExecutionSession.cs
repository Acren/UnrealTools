using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace LocalAutomation.Core;

/// <summary>
/// Represents a running or recently completed execution owned by the shared automation layers.
/// </summary>
public sealed class ExecutionSession
{
    private readonly Func<Task>? _cancelAsync;
    private readonly Dictionary<ExecutionTaskId, BufferedLogStream> _taskLogStreams = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionTaskStatus> _taskStatuses = new();
    private readonly Dictionary<ExecutionTaskId, string> _taskStatusReasons = new();
    private readonly Dictionary<ExecutionTaskId, List<ExecutionTaskId>> _childTaskIdsByParent = new();
    private readonly Dictionary<ExecutionTaskId, DateTimeOffset?> _taskStartedAt = new();
    private readonly Dictionary<ExecutionTaskId, DateTimeOffset?> _taskFinishedAt = new();

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

        // Seed per-task log streams up front when a previewable plan already exists so the UI can bind to stable empty
        // collections before a task has emitted its first line of output.
        if (plan != null)
        {
            foreach (ExecutionTask task in plan.Tasks)
            {
                _taskLogStreams[task.Id] = new BufferedLogStream();
                _taskStatuses[task.Id] = task.Status;
                _taskStatusReasons[task.Id] = task.StatusReason;
                _taskStartedAt[task.Id] = null;
                _taskFinishedAt[task.Id] = null;

                /* Cache the structural child relationships from the immutable plan once so later runtime status
                   propagation can cheaply update untouched descendants when a parent branch becomes terminal. */
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

    /// <summary>
    /// Raised whenever a task status changes during execution.
    /// </summary>
    public event Action<ExecutionTaskId, ExecutionTaskStatus, string?>? TaskStatusChanged;

    /// <summary>
    /// Gets the stable identifier for the execution session.
    /// </summary>
    public ExecutionSessionId Id { get; }

    /// <summary>
    /// Gets the immutable plan snapshot associated with this execution when one exists.
    /// </summary>
    public ExecutionPlan? Plan { get; }

    /// <summary>
    /// Gets the live log stream associated with this execution.
    /// </summary>
    public ILogStream LogStream { get; }

    /// <summary>
    /// Gets the known task-specific log streams for the execution.
    /// </summary>
    public IReadOnlyDictionary<ExecutionTaskId, BufferedLogStream> TaskLogStreams => new ReadOnlyDictionary<ExecutionTaskId, BufferedLogStream>(_taskLogStreams);

    /// <summary>
    /// Gets the current runtime task-status map for the session.
    /// </summary>
    public IReadOnlyDictionary<ExecutionTaskId, ExecutionTaskStatus> TaskStatuses => new ReadOnlyDictionary<ExecutionTaskId, ExecutionTaskStatus>(_taskStatuses);

    /// <summary>
    /// Gets the local time when the execution session was created.
    /// </summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Gets or sets the local time when the execution session finished.
    /// </summary>
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>
    /// Gets or sets whether the execution is currently running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Gets or sets the terminal outcome of the execution when it has finished.
    /// </summary>
    public RunOutcome? Outcome { get; set; }

    /// <summary>
    /// Gets or sets whether the execution completed successfully. This remains as a compatibility shim while the UI and
    /// runtime migrate to the richer outcome model.
    /// </summary>
    public bool? Success
    {
        get => Outcome == null ? null : Outcome == RunOutcome.Succeeded;
        set => Outcome = value == null ? null : (value.Value ? RunOutcome.Succeeded : RunOutcome.Failed);
    }

    /// <summary>
    /// Gets or sets the operation display name for the current session.
    /// </summary>
    public string OperationName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target display name for the current session.
    /// </summary>
    public string TargetName { get; set; } = string.Empty;

    /// <summary>
    /// Returns the direct and transitive descendants for one task id.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> GetTaskSubtreeIds(ExecutionTaskId taskId)
    {
        List<ExecutionTaskId> subtreeIds = new() { taskId };
        CollectDescendantTaskIds(taskId, subtreeIds);
        return subtreeIds;
    }

    /// <summary>
    /// Returns the effective runtime duration for a selected task, keeping the parent task open while any descendant is
    /// still active so branch timing matches the user's mental model of "this task is not done yet".
    /// </summary>
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

        /* Parent tasks are real tasks, but the user expectation here is branch-oriented: when selected child work is
           still running, the selected parent task should still look in progress. Use the earliest known start in the
           selected subtree and keep the end open until every task in that subtree is terminal. */
        foreach (ExecutionTaskId subtreeTaskId in subtreeIds)
        {
            if (_taskStartedAt.TryGetValue(subtreeTaskId, out DateTimeOffset? startedAt) && startedAt != null)
            {
                effectiveStart = effectiveStart == null || startedAt < effectiveStart ? startedAt : effectiveStart;
            }

            if (GetTaskStatus(subtreeTaskId) == ExecutionTaskStatus.Running)
            {
                effectiveEnd = timestampNow;
                continue;
            }

            if (_taskFinishedAt.TryGetValue(subtreeTaskId, out DateTimeOffset? finishedAt) && finishedAt != null)
            {
                if (effectiveEnd == null || finishedAt > effectiveEnd)
                {
                    effectiveEnd = finishedAt;
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

    /// <summary>
    /// Appends the provided log entry to the aggregate stream and, when a task id exists, to that task's stream as
    /// well so the UI can switch between session-wide and per-task output.
    /// </summary>
    public void AddLogEntry(LogEntry entry)
    {
        if (entry == null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        LogStream.Add(entry);
        if (entry.TaskId == null)
        {
            return;
        }

        EnsureTaskLogStream(entry.TaskId.Value).Add(entry);
    }

    /// <summary>
    /// Returns the buffered log stream for the provided task identifier, creating one when the session sees a task for
    /// the first time during runtime.
    /// </summary>
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

    /// <summary>
    /// Returns the task-specific log stream for the provided identifier when one exists.
    /// </summary>
    public BufferedLogStream? GetTaskLogStream(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return _taskLogStreams.TryGetValue(taskId.Value, out BufferedLogStream? logStream) ? logStream : null;
    }

    /// <summary>
    /// Updates one task's runtime status and raises a change event for graph-bound UI consumers.
    /// </summary>
    public void SetTaskStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null)
    {
        SetTaskStatusCore(taskId, status, statusReason);

        /* Session state starts with preview hierarchy copied from the plan. When a parent branch reaches a terminal
           runtime state before some descendants ever start, those untouched descendants must leave preview state too so
           the execution graph does not keep showing stale Planned/Pending children under a skipped/cancelled branch. */
        PropagateTerminalStatusToUntouchedDescendants(taskId, status, statusReason);
    }

    /// <summary>
    /// Transitions plan-derived preview tasks into runtime-ready session state when execution starts.
    /// </summary>
    public void BeginExecution()
    {
        /* The preview graph intentionally shows Planned, but once a real execution session exists every enabled task is
           part of that runtime session. Seed them as Pending up front so later scheduler or operation updates only need
           to describe real runtime transitions rather than also cleaning up stale preview state. */
        foreach (ExecutionTaskId taskId in _taskStatuses.Keys.ToList())
        {
            if (_taskStatuses[taskId] != ExecutionTaskStatus.Planned)
            {
                continue;
            }

            _taskStatuses[taskId] = ExecutionTaskStatus.Pending;
            _taskStatusReasons[taskId] = string.Empty;
        }
    }

    /// <summary>
    /// Returns the current runtime status for the provided task when one exists.
    /// </summary>
    public ExecutionTaskStatus? GetTaskStatus(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return _taskStatuses.TryGetValue(taskId.Value, out ExecutionTaskStatus status) ? status : null;
    }

    /// <summary>
    /// Returns the current runtime status reason for the provided task when one exists.
    /// </summary>
    public string? GetTaskStatusReason(ExecutionTaskId? taskId)
    {
        if (taskId == null)
        {
            return null;
        }

        return _taskStatusReasons.TryGetValue(taskId.Value, out string? statusReason) ? statusReason : null;
    }

    /// <summary>
    /// Cancels the underlying execution when cancellation is available.
    /// </summary>
    public Task CancelAsync()
    {
        return _cancelAsync != null ? _cancelAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// Records one task status change in the session map and notifies live listeners.
    /// </summary>
    private void SetTaskStatusCore(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason)
    {
        RecordTaskTiming(taskId, status);
        _taskStatuses[taskId] = status;
        _taskStatusReasons[taskId] = statusReason ?? string.Empty;
        TaskStatusChanged?.Invoke(taskId, status, statusReason);
    }

    /// <summary>
    /// Captures runtime task start and finish timestamps from status transitions so UI duration displays do not need to
    /// infer timing from log traffic.
    /// </summary>
    private void RecordTaskTiming(ExecutionTaskId taskId, ExecutionTaskStatus status)
    {
        DateTimeOffset timestamp = DateTimeOffset.Now;
        if (!_taskStartedAt.ContainsKey(taskId))
        {
            _taskStartedAt[taskId] = null;
        }

        if (!_taskFinishedAt.ContainsKey(taskId))
        {
            _taskFinishedAt[taskId] = null;
        }

        if (status == ExecutionTaskStatus.Running && _taskStartedAt[taskId] == null)
        {
            _taskStartedAt[taskId] = timestamp;
            _taskFinishedAt[taskId] = null;
            return;
        }

        if (status is ExecutionTaskStatus.Completed or ExecutionTaskStatus.Failed or ExecutionTaskStatus.Cancelled or ExecutionTaskStatus.Skipped or ExecutionTaskStatus.Disabled)
        {
            /* Some tasks can jump straight to a terminal state without an explicit Running transition, especially when
               execution policy disables or skips a whole branch. Seed a zero-or-near-zero duration from that terminal
               transition so selected task timing still exists even when no logs were emitted. */
            _taskStartedAt[taskId] ??= timestamp;
            _taskFinishedAt[taskId] = timestamp;
        }
    }

    /// <summary>
    /// Recursively collects descendant task ids from the cached parent-child map.
    /// </summary>
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

    /// <summary>
    /// Applies inherited terminal state to descendants that never started so hierarchical branches stay visually
    /// consistent with their parent task.
    /// </summary>
    private void PropagateTerminalStatusToUntouchedDescendants(ExecutionTaskId parentTaskId, ExecutionTaskStatus status, string? statusReason)
    {
        if (!_childTaskIdsByParent.TryGetValue(parentTaskId, out List<ExecutionTaskId>? childTaskIds))
        {
            return;
        }

        /* Only the parent task itself should show Failed or Cancelled when it was actively executing. Descendants that
           never started should become Skipped instead, while an explicitly skipped parent keeps the same semantic state
           for its untouched subtree. */
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
            ExecutionTaskStatus childStatus = _taskStatuses.TryGetValue(childTaskId, out ExecutionTaskStatus existingStatus)
                ? existingStatus
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
    /// Builds a clear inherited reason when a descendant is skipped because its parent branch stopped first.
    /// </summary>
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
}
