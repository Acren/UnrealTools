using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents the live runtime state for one execution-plan task within a specific execution session.
/// </summary>
public sealed class ExecutionTaskRuntimeState : INotifyPropertyChanged
{
    private ExecutionTaskStatus _status;
    private string _statusReason;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _finishedAt;

    public ExecutionTaskRuntimeState(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null)
    {
        TaskId = taskId;
        _status = status;
        _statusReason = statusReason ?? string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExecutionTaskId TaskId { get; }

    public ExecutionTaskStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string StatusReason
    {
        get => _statusReason;
        set => SetProperty(ref _statusReason, value ?? string.Empty);
    }

    public DateTimeOffset? StartedAt
    {
        get => _startedAt;
        set => SetProperty(ref _startedAt, value);
    }

    public DateTimeOffset? FinishedAt
    {
        get => _finishedAt;
        set => SetProperty(ref _finishedAt, value);
    }

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
