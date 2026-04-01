using System;
using System.ComponentModel;
using System.Threading;
using Avalonia.Threading;
using LocalAutomation.Core;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;
using RuntimeExecutionTaskMetrics = LocalAutomation.Runtime.ExecutionTaskMetrics;
using RuntimeExecutionTaskStatus = LocalAutomation.Runtime.ExecutionTaskStatus;
using RuntimeExecutionPlanTask = LocalAutomation.Runtime.ExecutionPlanTask;
using RuntimeExecutionSession = LocalAutomation.Runtime.ExecutionSession;
using RuntimeExecutionTaskRuntimeState = LocalAutomation.Runtime.ExecutionTaskRuntimeState;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Exposes one execution task's raw metadata plus runtime state for Avalonia surfaces without mixing in graph layout or
/// view-specific formatting.
/// </summary>
public sealed class ExecutionTaskViewModel : ViewModelBase, IDisposable
{
    private readonly RuntimeExecutionTaskRuntimeState? _runtimeState;
    private int _uiPostCount;
    private bool _isDisposed;

    /// <summary>
     /// Creates a shared Avalonia task view model from one execution-task model.
     /// </summary>
    public ExecutionTaskViewModel(RuntimeExecutionPlanTask task, RuntimeExecutionSession? session = null, RuntimeExecutionTaskRuntimeState? runtimeState = null)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Session = session;
        _runtimeState = runtimeState;

        if (_runtimeState != null)
        {
            _runtimeState.PropertyChanged += HandleRuntimeStatePropertyChanged;
        }
    }

    /// <summary>
    /// Gets the underlying immutable execution-task model.
    /// </summary>
    public RuntimeExecutionPlanTask Task { get; }

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
    /// Gets the raw current task status.
    /// </summary>
    public RuntimeExecutionTaskStatus Status
    {
        get => _runtimeState?.Status ?? (Task.Enabled ? RuntimeExecutionTaskStatus.Planned : RuntimeExecutionTaskStatus.Disabled);
    }

    /// <summary>
    /// Gets the explanatory text associated with the current status when one exists.
    /// </summary>
    public string StatusReason
    {
        get => _runtimeState?.StatusReason ?? (Task.Enabled ? string.Empty : Task.DisabledReason);
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
        if (_runtimeState != null)
        {
            _runtimeState.PropertyChanged -= HandleRuntimeStatePropertyChanged;
        }
    }

    /// <summary>
    /// Relays runtime-state property changes into the task view model's raw status and metrics properties.
    /// </summary>
    private void HandleRuntimeStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RaiseRuntimeStateProperties(e.PropertyName);
            return;
        }

        int postSequence = Interlocked.Increment(ref _uiPostCount);
        DateTime postedAtUtc = DateTime.UtcNow;
        Dispatcher.UIThread.Post(() =>
        {
            using PerformanceActivityScope activity = postSequence % 250 == 0
                ? PerformanceTelemetry.StartActivity("ExecutionTaskViewModel.RuntimeState.Dispatch")
                    .SetTag("task.id", Task.Id.Value)
                    .SetTag("task.title", Task.Title)
                    .SetTag("dispatch.post.sequence", postSequence)
                    .SetTag("dispatch.queue.delay_ms", (DateTime.UtcNow - postedAtUtc).TotalMilliseconds.ToString("0"))
                : default;
            RaiseRuntimeStateProperties(e.PropertyName);
        });
    }

    /// <summary>
    /// Applies the relevant property notifications for one runtime-state change.
    /// </summary>
    private void RaiseRuntimeStateProperties(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            RaisePropertyChanged(nameof(Status));
            RaisePropertyChanged(nameof(StatusReason));
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeExecutionTaskRuntimeState.Status), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(Status));
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeExecutionTaskRuntimeState.StatusReason), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(StatusReason));
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeExecutionTaskRuntimeState.StartedAt), StringComparison.Ordinal) ||
            string.Equals(propertyName, nameof(RuntimeExecutionTaskRuntimeState.FinishedAt), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(Metrics));
        }
    }
}
