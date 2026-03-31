using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LocalAutomation.Core;

/// <summary>
/// Represents the live runtime state for one execution-plan task within a specific execution session.
/// </summary>
public sealed class ExecutionTaskRuntimeState : INotifyPropertyChanged
{
    private ExecutionTaskStatus _status;
    private string _statusReason;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;

    /// <summary>
    /// Creates runtime state for one execution-plan task.
    /// </summary>
    public ExecutionTaskRuntimeState(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null)
    {
        TaskId = taskId;
        _status = status;
        _statusReason = statusReason ?? string.Empty;
    }

    /// <summary>
    /// Raised whenever one runtime-state property changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the stable execution-task identifier that owns this runtime state.
    /// </summary>
    public ExecutionTaskId TaskId { get; }

    /// <summary>
    /// Gets the current live status for the task.
    /// </summary>
    public ExecutionTaskStatus Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Gets the explanatory reason for the current status when one exists.
    /// </summary>
    public string StatusReason
    {
        get => _statusReason;
        internal set => SetProperty(ref _statusReason, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the time when the task first started running.
    /// </summary>
    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        internal set => SetProperty(ref _startedAt, value);
    }

    /// <summary>
    /// Gets the time when the task reached a terminal state.
    /// </summary>
    public DateTimeOffset? FinishedAt
    {
        get => _finishedAt;
        internal set => SetProperty(ref _finishedAt, value);
    }

    /// <summary>
    /// Updates a backing field and raises a property-changed notification only when the value actually changed.
    /// </summary>
    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
