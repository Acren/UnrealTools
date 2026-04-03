using System;
using System.ComponentModel;
using System.Threading;
using Avalonia.Threading;
using LocalAutomation.Core;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;
using RuntimeExecutionTaskMetrics = LocalAutomation.Runtime.ExecutionTaskMetrics;
using RuntimeExecutionTaskOutcome = LocalAutomation.Runtime.ExecutionTaskOutcome;
using RuntimeExecutionTaskState = LocalAutomation.Runtime.ExecutionTaskState;
using RuntimeExecutionTask = LocalAutomation.Runtime.ExecutionTask;
using RuntimeExecutionSession = LocalAutomation.Runtime.ExecutionSession;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Exposes one execution task's raw metadata plus runtime state for Avalonia surfaces without mixing in graph layout or
/// view-specific formatting.
/// </summary>
public sealed class ExecutionTaskViewModel : ViewModelBase, IDisposable
{
    private int _uiPostCount;
    private bool _isDisposed;

    /// <summary>
     /// Creates a shared Avalonia task view model from one execution-task model.
     /// </summary>
    public ExecutionTaskViewModel(RuntimeExecutionTask task, RuntimeExecutionSession? session = null)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Session = session;
        Task.PropertyChanged += HandleTaskPropertyChanged;
    }

    /// <summary>
    /// Gets the underlying live execution-task model.
    /// </summary>
    public RuntimeExecutionTask Task { get; }

    /// <summary>
    /// Gets the stable task identifier used across graph, logs, and runtime state.
    /// </summary>
    public RuntimeExecutionTaskId Id => Task.Id;

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
    public RuntimeExecutionTaskId? ParentId => Task.ParentId;

    /// <summary>
    /// Gets the raw execution state.
    /// </summary>
    public RuntimeExecutionTaskState State
    {
        get => Task.State;
    }

    /// <summary>
    /// Gets the semantic outcome once known.
    /// </summary>
    public RuntimeExecutionTaskOutcome? Outcome => Task.Outcome;

    /// <summary>
    /// Gets the combined display status rendered by Avalonia surfaces.
    /// </summary>
    public ExecutionTaskDisplayStatus Status => DisplayStatus;

    /// <summary>
    /// Gets the primary semantic status rendered by the UI, falling back to lifecycle state while no semantic outcome is
    /// known yet.
    /// </summary>
    public ExecutionTaskDisplayStatus DisplayStatus => ExecutionTaskStatusDisplay.GetDisplayStatus(State, Outcome);

    /// <summary>
    /// Gets the explanatory text associated with the current lifecycle/result state when one exists.
    /// </summary>
    public string StatusReason
    {
        get => Task.StatusReason;
    }

    /// <summary>
    /// Gets the raw runtime metrics for this task or task subtree.
    /// </summary>
    public RuntimeExecutionTaskMetrics Metrics
    {
        get => Session?.GetTaskMetrics(Task.Id) ?? RuntimeExecutionTaskMetrics.Empty;
    }

    /// <summary>
    /// Gets the backing execution session when this task belongs to a live or completed runtime tab.
    /// </summary>
    private RuntimeExecutionSession? Session { get; }

    /// <summary>
    /// Raises duration-sensitive property changes while the task or one of its descendants is still active.
    /// </summary>
    public void RefreshTimeSensitiveState()
    {
        if (Session != null)
        {
            RaisePropertyChanged(nameof(Metrics));
        }
    }

    /// <summary>
    /// Disposes session/runtime subscriptions owned by this task view model.
     /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Task.PropertyChanged -= HandleTaskPropertyChanged;
    }

    /// <summary>
    /// Relays task property changes into the task view model's raw status and metrics properties.
    /// </summary>
    private void HandleTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RaiseTaskProperties(e.PropertyName);
            return;
        }

        int postSequence = Interlocked.Increment(ref _uiPostCount);
        DateTime postedAtUtc = DateTime.UtcNow;
        Dispatcher.UIThread.Post(() =>
        {
            using PerformanceActivityScope activity = postSequence % 250 == 0
                ? PerformanceTelemetry.StartActivity("ExecutionTaskViewModel.Task.Dispatch")
                    .SetTag("task.id", Task.Id.Value)
                    .SetTag("task.title", Task.Title)
                    .SetTag("dispatch.post.sequence", postSequence)
                    .SetTag("dispatch.queue.delay_ms", (DateTime.UtcNow - postedAtUtc).TotalMilliseconds.ToString("0"))
                : default;
            RaiseTaskProperties(e.PropertyName);
        });
    }

    /// <summary>
    /// Applies the relevant property notifications for one task-state change.
    /// </summary>
    private void RaiseTaskProperties(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            RaisePropertyChanged(nameof(State));
            RaisePropertyChanged(nameof(Outcome));
            RaisePropertyChanged(nameof(Status));
            RaisePropertyChanged(nameof(DisplayStatus));
            RaisePropertyChanged(nameof(StatusReason));
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeExecutionTask.State), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(State));
            RaisePropertyChanged(nameof(Status));
            RaisePropertyChanged(nameof(DisplayStatus));
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeExecutionTask.Outcome), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(Outcome));
            RaisePropertyChanged(nameof(Status));
            RaisePropertyChanged(nameof(DisplayStatus));
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeExecutionTask.StatusReason), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(StatusReason));
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeExecutionTask.StartedAt), StringComparison.Ordinal) ||
            string.Equals(propertyName, nameof(RuntimeExecutionTask.FinishedAt), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(Metrics));
        }
    }
}
