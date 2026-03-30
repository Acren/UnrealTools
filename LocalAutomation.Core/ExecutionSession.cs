using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace LocalAutomation.Core;

/// <summary>
/// Represents a running or recently completed execution owned by the shared automation layers.
/// </summary>
public sealed class ExecutionSession
{
    private readonly Func<Task>? _cancelAsync;
    private readonly Dictionary<string, BufferedLogStream> _taskLogStreams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionTaskStatus> _taskStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _taskStatusReasons = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an execution session around a shared log stream and optional cancellation action.
    /// </summary>
    public ExecutionSession(ILogStream logStream, Func<Task>? cancelAsync = null, ExecutionPlan? plan = null)
    {
        Id = Guid.NewGuid().ToString("N");
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
            }
        }
    }

    /// <summary>
    /// Raised whenever a task status changes during execution.
    /// </summary>
    public event Action<string, ExecutionTaskStatus, string?>? TaskStatusChanged;

    /// <summary>
    /// Gets the stable identifier for the execution session.
    /// </summary>
    public string Id { get; }

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
    public IReadOnlyDictionary<string, BufferedLogStream> TaskLogStreams => new ReadOnlyDictionary<string, BufferedLogStream>(_taskLogStreams);

    /// <summary>
    /// Gets the current runtime task-status map for the session.
    /// </summary>
    public IReadOnlyDictionary<string, ExecutionTaskStatus> TaskStatuses => new ReadOnlyDictionary<string, ExecutionTaskStatus>(_taskStatuses);

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
        if (string.IsNullOrWhiteSpace(entry.TaskId))
        {
            return;
        }

        EnsureTaskLogStream(entry.TaskId).Add(entry);
    }

    /// <summary>
    /// Returns the buffered log stream for the provided task identifier, creating one when the session sees a task for
    /// the first time during runtime.
    /// </summary>
    public BufferedLogStream EnsureTaskLogStream(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("Execution task id is required.", nameof(taskId));
        }

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
    public BufferedLogStream? GetTaskLogStream(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        return _taskLogStreams.TryGetValue(taskId, out BufferedLogStream? logStream) ? logStream : null;
    }

    /// <summary>
    /// Updates one task's runtime status and raises a change event for graph-bound UI consumers.
    /// </summary>
    public void SetTaskStatus(string taskId, ExecutionTaskStatus status, string? statusReason = null)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("Execution task id is required.", nameof(taskId));
        }

        _taskStatuses[taskId] = status;
        _taskStatusReasons[taskId] = statusReason ?? string.Empty;
        TaskStatusChanged?.Invoke(taskId, status, statusReason);
    }

    /// <summary>
    /// Returns the current runtime status for the provided task when one exists.
    /// </summary>
    public ExecutionTaskStatus? GetTaskStatus(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        return _taskStatuses.TryGetValue(taskId, out ExecutionTaskStatus status) ? status : null;
    }

    /// <summary>
    /// Returns the current runtime status reason for the provided task when one exists.
    /// </summary>
    public string? GetTaskStatusReason(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return null;
        }

        return _taskStatusReasons.TryGetValue(taskId, out string? statusReason) ? statusReason : null;
    }

    /// <summary>
    /// Cancels the underlying execution when cancellation is available.
    /// </summary>
    public Task CancelAsync()
    {
        return _cancelAsync != null ? _cancelAsync() : Task.CompletedTask;
    }
}
