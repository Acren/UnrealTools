using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Threading;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Exposes one execution task's raw metadata plus runtime state for Avalonia surfaces without mixing in graph layout or
/// view-specific formatting.
/// </summary>
public sealed class ExecutionTaskViewModel : ViewModelBase, IDisposable
{
    private readonly ExecutionSession? _session;
    private readonly ExecutionTaskRuntimeState? _runtimeState;
    private readonly IReadOnlySet<ExecutionTaskId> _subtreeTaskIds;
    private bool _isDisposed;

    /// <summary>
     /// Creates a shared Avalonia task view model from one execution-task model.
     /// </summary>
    public ExecutionTaskViewModel(ExecutionPlanTask task, ExecutionSession? session = null, ExecutionTaskRuntimeState? runtimeState = null, IReadOnlySet<ExecutionTaskId>? subtreeTaskIds = null)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        _session = session;
        _runtimeState = runtimeState;
        _subtreeTaskIds = subtreeTaskIds ?? new HashSet<ExecutionTaskId> { task.Id };

        if (_runtimeState != null)
        {
            _runtimeState.PropertyChanged += HandleRuntimeStatePropertyChanged;
        }

        if (_session != null)
        {
            _session.LogStream.EntryAdded += HandleSessionLogEntryAdded;
        }
    }

    /// <summary>
    /// Gets the underlying immutable execution-task model.
    /// </summary>
    public ExecutionPlanTask Task { get; }

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
        get => _runtimeState?.Status ?? Task.Status;
    }

    /// <summary>
    /// Gets the explanatory text associated with the current status when one exists.
    /// </summary>
    public string StatusReason
    {
        get => _runtimeState?.StatusReason ?? Task.StatusReason;
    }

    /// <summary>
    /// Gets the raw runtime metrics for this task or task subtree.
    /// </summary>
    public ExecutionTaskMetrics Metrics
    {
        get => _session?.GetTaskMetrics(Task.Id) ?? ExecutionTaskMetrics.Empty;
    }

    /// <summary>
    /// Raises duration-sensitive property changes while the task or one of its descendants is still active.
    /// </summary>
    public void RefreshTimeSensitiveState()
    {
        if (_session != null)
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

        if (_session != null)
        {
            _session.LogStream.EntryAdded -= HandleSessionLogEntryAdded;
        }
    }

    /// <summary>
    /// Relays runtime-state property changes into the task view model's raw status and metrics properties.
    /// </summary>
    private void HandleRuntimeStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PostToUiThread(() =>
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName))
            {
                RaisePropertyChanged(nameof(Status));
                RaisePropertyChanged(nameof(StatusReason));
                return;
            }

            if (string.Equals(e.PropertyName, nameof(ExecutionTaskRuntimeState.Status), StringComparison.Ordinal))
            {
                RaisePropertyChanged(nameof(Status));
                return;
            }

            if (string.Equals(e.PropertyName, nameof(ExecutionTaskRuntimeState.StatusReason), StringComparison.Ordinal))
            {
                RaisePropertyChanged(nameof(StatusReason));
                return;
            }

            if (string.Equals(e.PropertyName, nameof(ExecutionTaskRuntimeState.StartedAt), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(ExecutionTaskRuntimeState.FinishedAt), StringComparison.Ordinal))
            {
                RaisePropertyChanged(nameof(Metrics));
            }
        });
    }

    /// <summary>
    /// Refreshes metrics on task-scoped log entries that affect this task's subtree so the VM remains self-contained and
    /// relies on the session/runtime-state layer rather than workspace code pushing snapshots into it.
    /// </summary>
    private void HandleSessionLogEntryAdded(LogEntry entry)
    {
        if (_runtimeState == null || entry.TaskId is not ExecutionTaskId taskId || !_subtreeTaskIds.Contains(taskId))
        {
            return;
        }

        PostToUiThread(() => RaisePropertyChanged(nameof(Metrics)));
    }

    /// <summary>
    /// Marshals task-view-model notifications to Avalonia's UI thread because runtime state changes originate from worker threads.
    /// </summary>
    private static void PostToUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }
}
