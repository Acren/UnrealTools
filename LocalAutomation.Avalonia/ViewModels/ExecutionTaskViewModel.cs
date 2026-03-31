using System;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Exposes one execution task's raw metadata plus runtime state for Avalonia surfaces without mixing in graph layout or
/// view-specific formatting.
/// </summary>
public sealed class ExecutionTaskViewModel : ViewModelBase
{
    private ExecutionTaskStatus _status;
    private string _statusReason;
    private ExecutionTaskMetrics _metrics;

    /// <summary>
    /// Creates a shared Avalonia task view model from one execution-task model.
    /// </summary>
    public ExecutionTaskViewModel(ExecutionTask task)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        _status = task.Status;
        _statusReason = task.StatusReason;
    }

    /// <summary>
    /// Gets the underlying immutable execution-task model.
    /// </summary>
    public ExecutionTask Task { get; }

    /// <summary>
    /// Gets the stable task identifier used across graph, logs, and runtime state.
    /// </summary>
    public ExecutionTaskId Id => Task.Id;

    /// <summary>
    /// Gets the short display title for the task.
    /// </summary>
    public string Title => Task.Title;

    /// <summary>
    /// Gets the longer descriptive text for the task when one exists.
    /// </summary>
    public string Description => Task.Description;

    /// <summary>
    /// Gets the parent task identifier when this task participates in the execution hierarchy.
    /// </summary>
    public ExecutionTaskId? ParentId => Task.ParentId;

    /// <summary>
    /// Gets the raw current task status.
    /// </summary>
    public ExecutionTaskStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Gets the explanatory text associated with the current status when one exists.
    /// </summary>
    public string StatusReason
    {
        get => _statusReason;
        private set => SetProperty(ref _statusReason, value);
    }

    /// <summary>
    /// Gets the raw runtime metrics for this task or task subtree.
    /// </summary>
    public ExecutionTaskMetrics Metrics
    {
        get => _metrics;
        private set => SetProperty(ref _metrics, value);
    }

    /// <summary>
    /// Updates the raw runtime status for the task.
    /// </summary>
    public void SetStatus(ExecutionTaskStatus status, string? statusReason)
    {
        Status = status;
        StatusReason = statusReason ?? string.Empty;
    }

    /// <summary>
    /// Updates the raw runtime metrics for the task.
    /// </summary>
    public void SetMetrics(ExecutionTaskMetrics metrics)
    {
        Metrics = metrics;
    }
}
