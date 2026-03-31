using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using LocalAutomation.Core;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Owns the execution workspace tabs, plan preview, task-scoped log selection, and live execution-session lifecycle
/// actions for the Avalonia shell.
/// </summary>
public sealed class ExecutionWorkspaceViewModel : ViewModelBase
{
    private static readonly ObservableCollection<LogEntryViewModel> EmptyLogEntries = new();

    private const int MaxLogEntriesPerFlush = 100;
    private static readonly TimeSpan PendingLogFlushInterval = TimeSpan.FromMilliseconds(50);

    private readonly LocalAutomationApplicationHost _services;
    private readonly object _pendingLogSyncRoot = new();
    private readonly Action<string> _setStatus;
    private readonly DispatcherTimer _pendingLogFlushTimer;
    private readonly DispatcherTimer _runtimeDurationTimer;
    private readonly Dictionary<RuntimeWorkspaceTabViewModel, Queue<LogEntryViewModel>> _pendingLogEntries = new();
    private readonly Dictionary<RuntimeWorkspaceTabViewModel, ILogStream> _attachedLogStreams = new();
    private readonly Dictionary<RuntimeWorkspaceTabViewModel, ExecutionSession> _attachedSessions = new();
    private bool _isPendingLogFlushStartQueued;
    private RuntimeWorkspaceTabViewModel? _observedWorkspaceTab;
    private RuntimeWorkspaceTabViewModel? _selectedRuntimeTab;
    private event PropertyChangedEventHandler? _selectedRuntimeTabPropertyChanged;

    /// <summary>
    /// Creates the execution workspace view model around the shared services and shell status sink.
    /// </summary>
    public ExecutionWorkspaceViewModel(LocalAutomationApplicationHost services, Action<string> setStatus)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _pendingLogFlushTimer = new DispatcherTimer { Interval = PendingLogFlushInterval };
        _pendingLogFlushTimer.Tick += HandlePendingLogFlushTimerTick;
        _runtimeDurationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _runtimeDurationTimer.Tick += HandleRuntimeDurationTimerTick;

        RuntimeTabs.Add(CreateApplicationLogTab());
        RuntimeTabs.Add(CreatePlanPreviewTab());
        SelectedRuntimeTab = RuntimeTabs[1];
        AttachApplicationLogStream();
    }

    /// <summary>
    /// Gets the runtime workspace tabs. The first two tabs are the permanent application-log and plan-preview tabs.
    /// </summary>
    public ObservableCollection<RuntimeWorkspaceTabViewModel> RuntimeTabs { get; } = new();

    /// <summary>
    /// Gets or sets the currently selected runtime workspace tab.
    /// </summary>
    public RuntimeWorkspaceTabViewModel? SelectedRuntimeTab
    {
        get => _selectedRuntimeTab;
        set
        {
            if (!SetProperty(ref _selectedRuntimeTab, value))
            {
                return;
            }

            if (_selectedRuntimeTabPropertyChanged != null)
            {
                if (_observedWorkspaceTab != null)
                {
                    _observedWorkspaceTab.PropertyChanged -= _selectedRuntimeTabPropertyChanged;
                }

                _selectedRuntimeTabPropertyChanged = null;
                _observedWorkspaceTab = null;
            }

            if (value != null)
            {
                _selectedRuntimeTabPropertyChanged = (_, args) => HandleSelectedRuntimeTabPropertyChanged(args.PropertyName);
                value.PropertyChanged += _selectedRuntimeTabPropertyChanged;
                _observedWorkspaceTab = value;
            }

            foreach (RuntimeWorkspaceTabViewModel tab in RuntimeTabs)
            {
                tab.IsSelected = ReferenceEquals(tab, value);
            }

            RaiseSelectionStateChanged();
        }
    }

    /// <summary>
    /// Gets whether the selected runtime tab currently represents active work that can be terminated.
    /// </summary>
    public bool IsRunning => SelectedRuntimeTab?.CanTerminate == true;

    /// <summary>
    /// Gets a short summary line for the selected runtime workspace tab.
    /// </summary>
    public string ExecutionSummary
    {
        get
        {
            if (SelectedRuntimeTab == null)
            {
                return "No runtime tab is selected.";
            }

            if (SelectedRuntimeTab.IsApplicationLog)
            {
                return "Application log messages appear here even when no tasks are running.";
            }

            if (SelectedRuntimeTab.IsPlanPreview)
            {
                return "The Plan tab previews the task graph for the current target, operation, and option values.";
            }

            if (SelectedRuntimeTab.Session?.IsRunning == true)
            {
                return $"Running {SelectedRuntimeTab.Session.OperationName} on {SelectedRuntimeTab.Session.TargetName}";
            }

            if (SelectedRuntimeTab.Session?.Outcome == RunOutcome.Succeeded)
            {
                return $"{SelectedRuntimeTab.Session.OperationName} succeeded for {SelectedRuntimeTab.Session.TargetName}";
            }

            if (SelectedRuntimeTab.Session?.Outcome == RunOutcome.Cancelled)
            {
                return $"{SelectedRuntimeTab.Session.OperationName} was cancelled for {SelectedRuntimeTab.Session.TargetName}";
            }

            if (SelectedRuntimeTab.Session != null)
            {
                return $"{SelectedRuntimeTab.Session.OperationName} failed for {SelectedRuntimeTab.Session.TargetName}";
            }

            return SelectedRuntimeTab.Subtitle;
        }
    }

    /// <summary>
    /// Gets the log entries shown for the selected graph node, or the full session log when nothing is selected.
    /// </summary>
    public ObservableCollection<LogEntryViewModel> SelectedRuntimeLogEntries => SelectedRuntimeTab?.SelectedLogEntries ?? EmptyLogEntries;

    /// <summary>
    /// Gets the stable logical source id for the currently displayed log stream so the reusable log viewer can reset
    /// follow-tail only when the user actually switches to a different source.
    /// </summary>
    public string SelectedRuntimeLogSourceId
    {
        get
        {
            if (SelectedRuntimeTab == null)
            {
                return string.Empty;
            }

            if (SelectedRuntimeTab.IsApplicationLog)
            {
                return $"{SelectedRuntimeTab.Id}:application-log";
            }

            if (SelectedRuntimeTab.IsPlanPreview)
            {
                return $"{SelectedRuntimeTab.Id}:plan-preview";
            }

            if (SelectedRuntimeTab.Graph.SelectedTaskId == null)
            {
                return $"{SelectedRuntimeTab.Id}:session-output";
            }

            return $"{SelectedRuntimeTab.Id}:{SelectedRuntimeTab.Graph.SelectedTaskId.Value}";
        }
    }

    /// <summary>
    /// Gets the selected runtime-tab title shown in the workspace header.
    /// </summary>
    public string SelectedRuntimeTabTitle => SelectedRuntimeTab?.Title ?? "Runtime";

    /// <summary>
     /// Gets whether the selected runtime tab should show task metrics in the header.
     /// </summary>
    public bool ShowSelectedRuntimeMetrics => SelectedRuntimeTab?.ShowsRuntimeMetrics == true;

    /// <summary>
    /// Gets the selected task view model when the graph selection corresponds to a real task.
    /// </summary>
    public ExecutionTaskViewModel? SelectedTask => SelectedRuntimeTab?.Graph.SelectedNode?.Task;

    /// <summary>
    /// Gets the shared metrics shown in the selected runtime-tab header.
    /// </summary>
    public ExecutionTaskMetrics SelectedRuntimeMetrics
    {
        get
        {
            if (SelectedRuntimeTab?.Session is not ExecutionSession session)
            {
                return ExecutionTaskMetrics.Empty;
            }

            return SelectedTask?.Metrics ?? session.GetTaskMetrics(SelectedRuntimeTab.Graph.SelectedTaskId);
        }
    }

    /// <summary>
    /// Gets the graph view model for the currently selected workspace tab when that tab renders a graph pane.
    /// </summary>
    public ExecutionGraphViewModel? SelectedGraph => SelectedRuntimeTab?.ShowsGraph == true ? SelectedRuntimeTab.Graph : null;

    /// <summary>
    /// Refreshes the permanent plan-preview tab from the latest selected target, operation, and option values.
    /// </summary>
    public void UpdatePlanPreview(ExecutionPlan? plan)
    {
        /* Trace the workspace-level preview update separately from shell refresh so the timing tree shows whether the
           cost sits in the plan graph rebuild or elsewhere in the surrounding view-model refresh. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("UpdatePlanPreview");
        PerformanceTelemetry.SetTag(activity, "plan.has_result", plan != null);
        PerformanceTelemetry.SetTag(activity, "plan.task.count", plan?.Tasks.Count ?? 0);

        RuntimeWorkspaceTabViewModel planTab = RuntimeTabs.First(tab => tab.Kind == RuntimeWorkspaceTabKind.PlanPreview);
        planTab.SetTasks(plan?.Tasks ?? Array.Empty<ExecutionPlanTask>());
        planTab.Graph.SetPlan(plan);
        RebuildTabSelectedLogEntries(planTab);

        if (ReferenceEquals(SelectedRuntimeTab, planTab))
        {
            RaiseSelectionStateChanged();
        }
    }

    /// <summary>
    /// Adds a newly started execution session to the workspace and begins mirroring its plan, task states, and logs.
    /// </summary>
    public void AttachExecutionSession(ExecutionSession session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        ExecutionGraphViewModel graph = new();
        RuntimeWorkspaceTabViewModel runtimeTab = new(
            id: session.Id.Value,
            title: session.OperationName,
            subtitle: session.TargetName,
            kind: RuntimeWorkspaceTabKind.ExecutionSession,
            presentation: new RuntimeWorkspaceTabPresentation(showGraph: true, showLog: true, showSubtitle: true, showStatusMarker: true, showRuntimeMetrics: true),
            graph: graph,
            session: session);
        runtimeTab.SetTasks(session.Plan?.Tasks ?? Array.Empty<ExecutionPlanTask>());

        graph.AttachTasks(runtimeTab.TasksById);
        graph.SetPlan(session.Plan);

        HookGraphSelection(runtimeTab);
        RuntimeTabs.Add(runtimeTab);
        SelectedRuntimeTab = runtimeTab;
        if (!_runtimeDurationTimer.IsEnabled)
        {
            _runtimeDurationTimer.Start();
        }

        AttachSessionLogs(runtimeTab, session);
        session.TaskStatusChanged += (taskId, status, statusReason) => Dispatcher.UIThread.Post(() =>
        {
            runtimeTab.Graph.NotifyTaskStateChanged(taskId);
            if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                RaiseSelectionStateChanged();
            }
        });

        _attachedSessions[runtimeTab] = session;
        _ = WatchExecutionCompletionAsync(runtimeTab);
        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Cancels the selected execution session when one is currently running.
    /// </summary>
    public async Task CancelExecutionAsync()
    {
        if (SelectedRuntimeTab?.Session is not { IsRunning: true } session)
        {
            return;
        }

        _setStatus($"Cancelling {session.OperationName}.");
        await session.CancelAsync();
        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Clears the active log stream for the selected tab and graph selection.
    /// </summary>
    public void ClearSelectedRuntimeLog()
    {
        if (SelectedRuntimeTab == null)
        {
            return;
        }

        if (SelectedRuntimeTab.IsApplicationLog)
        {
            ApplicationLogService.LogStream.Clear();
            RebuildTabSelectedLogEntries(SelectedRuntimeTab);
            _setStatus("Cleared application log output.");
            RaiseSelectionStateChanged();
            return;
        }

        if (SelectedRuntimeTab.Session != null)
        {
            ExecutionTaskId? selectedTaskId = SelectedRuntimeTab.Graph.SelectedTaskId;
            if (selectedTaskId == null)
            {
                SelectedRuntimeTab.Session.LogStream.Clear();
            }
            else
            {
                SelectedRuntimeTab.Session.GetTaskLogStream(selectedTaskId)?.Clear();
            }
        }

        RemovePendingLogEntries(SelectedRuntimeTab);
        RebuildTabSelectedLogEntries(SelectedRuntimeTab);
        _setStatus($"Cleared log output for {SelectedRuntimeTab.Title.ToLowerInvariant()}." );
        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Closes the provided runtime tab when it is not one of the permanent workspace tabs.
    /// </summary>
    public async Task CloseRuntimeTabAsync(RuntimeWorkspaceTabViewModel? runtimeTab)
    {
        if (runtimeTab == null || !runtimeTab.CanClose)
        {
            return;
        }

        if (runtimeTab.Session is { IsRunning: true } session)
        {
            await session.CancelAsync();
            _setStatus($"Cancelling {session.OperationName} before closing its tab.");
        }

        RemoveRuntimeTab(runtimeTab);
    }

    /// <summary>
    /// Selects one graph node inside the provided runtime tab.
    /// </summary>
    public void SelectGraphNode(RuntimeWorkspaceTabViewModel runtimeTab, ExecutionNodeViewModel? node)
    {
        if (runtimeTab == null)
        {
            throw new ArgumentNullException(nameof(runtimeTab));
        }

        runtimeTab.Graph.SelectNode(node);
        RebuildTabSelectedLogEntries(runtimeTab);

        if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
        {
            RaiseSelectionStateChanged();
        }
    }

    /// <summary>
    /// Polls the running session until it completes so completion state can be reflected back into the workspace.
    /// </summary>
    private async Task WatchExecutionCompletionAsync(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        ExecutionSession session = runtimeTab.Session!;
        while (session.IsRunning)
        {
            await Task.Delay(100);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            session.FinishedAt = DateTimeOffset.Now;
            runtimeTab.NotifyStateChanged();
            if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                RaiseSelectionStateChanged();
            }

            if (session.Outcome == RunOutcome.Succeeded)
            {
                _setStatus($"{session.OperationName} succeeded for {session.TargetName}.");
            }
            else if (session.Outcome == RunOutcome.Cancelled)
            {
                _setStatus($"{session.OperationName} was cancelled for {session.TargetName}.");
            }
            else
            {
                _setStatus($"{session.OperationName} finished with failure for {session.TargetName}.");
            }
        });
    }

    /// <summary>
    /// Removes a runtime workspace tab and selects a neighboring tab when the removed tab was active.
    /// </summary>
    private void RemoveRuntimeTab(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        RemovePendingLogEntries(runtimeTab);
        _attachedLogStreams.Remove(runtimeTab);
        _attachedSessions.Remove(runtimeTab);
        runtimeTab.DisposeTasks();

        int removedIndex = RuntimeTabs.IndexOf(runtimeTab);
        RuntimeTabs.Remove(runtimeTab);

        if (runtimeTab.Session != null)
        {
            _services.Execution.RemoveSession(runtimeTab.Session.Id);
        }

        if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
        {
            int nextIndex = Math.Clamp(removedIndex - 1, 0, RuntimeTabs.Count - 1);
            SelectedRuntimeTab = RuntimeTabs.Count > 0 ? RuntimeTabs[nextIndex] : null;
        }

        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Seeds the application-log tab from the shared process-wide log stream and keeps listening for new lines.
    /// </summary>
    private void AttachApplicationLogStream()
    {
        RuntimeWorkspaceTabViewModel applicationTab = RuntimeTabs.First(tab => tab.Kind == RuntimeWorkspaceTabKind.ApplicationLog);
        HookGraphSelection(applicationTab);
        AttachLogStream(applicationTab, ApplicationLogService.LogStream);
    }

    /// <summary>
    /// Attaches the aggregate log stream for one execution session. Task and subtree views are rebuilt by filtering the
    /// session-wide stream so hierarchical selections stay correct even when descendant tasks are created dynamically.
    /// </summary>
    private void AttachSessionLogs(RuntimeWorkspaceTabViewModel runtimeTab, ExecutionSession session)
    {
        AttachLogStream(runtimeTab, session.LogStream);

        /* Graph and header metrics are derived from session state and logs, so one new task log line only needs to
           trigger a UI re-read rather than pushing cached metric snapshots around. */
        session.LogStream.EntryAdded += entry => Dispatcher.UIThread.Post(() =>
        {
            if (entry.TaskId is ExecutionTaskId taskId)
            {
                runtimeTab.Graph.NotifyTaskStateChanged(taskId);
            }

            if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                RaisePropertyChanged(nameof(SelectedRuntimeMetrics));
            }
        });
    }

    /// <summary>
    /// Attaches one log stream to a workspace tab and seeds the currently visible log pane from its buffered entries.
    /// </summary>
    private void AttachLogStream(RuntimeWorkspaceTabViewModel runtimeTab, ILogStream logStream)
    {
        _attachedLogStreams[runtimeTab] = logStream;
        RebuildTabSelectedLogEntries(runtimeTab);
        logStream.EntryAdded += entry => EnqueuePendingLogEntry(runtimeTab, CreateLogEntryViewModel(entry));
    }

    /// <summary>
    /// Hooks the graph selection so the details pane and scoped log stream switch immediately when the user clicks a
    /// different node on the canvas.
    /// </summary>
    private void HookGraphSelection(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        runtimeTab.Graph.PropertyChanged += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.PropertyName) &&
                !string.Equals(args.PropertyName, nameof(ExecutionGraphViewModel.SelectedNode), StringComparison.Ordinal))
            {
                return;
            }

            RebuildTabSelectedLogEntries(runtimeTab);
            if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                RaiseSelectionStateChanged();
            }
        };
    }

    /// <summary>
    /// Rebuilds the selected log pane contents for one workspace tab from the appropriate aggregate or task-scoped log
    /// stream.
    /// </summary>
    private void RebuildTabSelectedLogEntries(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        if (runtimeTab.IsApplicationLog)
        {
            runtimeTab.SetSelectedLogEntries(ApplicationLogService.LogStream.Entries.Select(CreateLogEntryViewModel));
            return;
        }

        if (runtimeTab.Session == null)
        {
            runtimeTab.SetSelectedLogEntries(Array.Empty<LogEntryViewModel>());
            return;
        }

        IReadOnlyList<ExecutionTaskId> selectedTaskIds = runtimeTab.Graph.GetSelectedLogTaskIds();
        if (selectedTaskIds.Count == 0)
        {
            runtimeTab.SetSelectedLogEntries(runtimeTab.Session.LogStream.Entries.Select(CreateLogEntryViewModel));
            return;
        }

        HashSet<ExecutionTaskId> selectedTaskIdSet = new(selectedTaskIds);
        runtimeTab.SetSelectedLogEntries(runtimeTab.Session.LogStream.Entries
            .Where(entry => entry.TaskId is ExecutionTaskId taskId && selectedTaskIdSet.Contains(taskId))
            .Select(CreateLogEntryViewModel));
    }

    /// <summary>
    /// Buffers one pending log entry or task-log refresh so high-volume output does not schedule one dispatcher callback
    /// per line.
    /// </summary>
    private void EnqueuePendingLogEntry(RuntimeWorkspaceTabViewModel runtimeTab, LogEntryViewModel entry)
    {
        bool queueFlushStart;
        lock (_pendingLogSyncRoot)
        {
            if (!_pendingLogEntries.TryGetValue(runtimeTab, out Queue<LogEntryViewModel>? pendingEntries))
            {
                pendingEntries = new Queue<LogEntryViewModel>();
                _pendingLogEntries[runtimeTab] = pendingEntries;
            }

            pendingEntries.Enqueue(entry);
            queueFlushStart = !_pendingLogFlushTimer.IsEnabled && !_isPendingLogFlushStartQueued;
            if (queueFlushStart)
            {
                _isPendingLogFlushStartQueued = true;
            }
        }

        if (!queueFlushStart)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_pendingLogSyncRoot)
            {
                _isPendingLogFlushStartQueued = false;
                if (_pendingLogEntries.Count == 0)
                {
                    return;
                }
            }

            if (!_pendingLogFlushTimer.IsEnabled)
            {
                _pendingLogFlushTimer.Start();
            }
        });
    }

    /// <summary>
    /// Requests a bounded refresh pass for the selected task log without needing to synthesize a fake new log line.
    /// </summary>
    private void EnqueueRefreshForSelection(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        EnqueuePendingLogEntry(runtimeTab, new LogEntryViewModel(string.Empty, Microsoft.Extensions.Logging.LogLevel.Trace));
    }

    /// <summary>
    /// Drops any queued log lines for a workspace tab that is being cleared or removed.
    /// </summary>
    private void RemovePendingLogEntries(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        bool shouldStopTimer;
        lock (_pendingLogSyncRoot)
        {
            _pendingLogEntries.Remove(runtimeTab);
            shouldStopTimer = _pendingLogEntries.Count == 0;
        }

        if (shouldStopTimer && _pendingLogFlushTimer.IsEnabled)
        {
            _pendingLogFlushTimer.Stop();
        }
    }

    /// <summary>
    /// Flushes pending log updates onto the selected log pane in bounded batches.
    /// </summary>
    private void HandlePendingLogFlushTimerTick(object? sender, EventArgs e)
    {
        List<RuntimeWorkspaceTabViewModel> tabsToRefresh = DrainPendingLogEntries(out bool hasMorePendingEntries);
        bool selectedTabChanged = false;
        foreach (RuntimeWorkspaceTabViewModel runtimeTab in tabsToRefresh)
        {
            RebuildTabSelectedLogEntries(runtimeTab);
            if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                selectedTabChanged = true;
            }
        }

        if (selectedTabChanged)
        {
            RaiseSelectionStateChanged();
        }

        if (!hasMorePendingEntries)
        {
            _pendingLogFlushTimer.Stop();
        }
    }

    /// <summary>
    /// Drains a bounded number of queued updates while preserving tab-local ordering.
    /// </summary>
    private List<RuntimeWorkspaceTabViewModel> DrainPendingLogEntries(out bool hasMorePendingEntries)
    {
        List<RuntimeWorkspaceTabViewModel> tabsToRefresh = new();
        lock (_pendingLogSyncRoot)
        {
            int remainingBudget = MaxLogEntriesPerFlush;
            foreach ((RuntimeWorkspaceTabViewModel runtimeTab, Queue<LogEntryViewModel> pendingEntries) in _pendingLogEntries.ToList())
            {
                if (remainingBudget == 0)
                {
                    break;
                }

                int entriesToDrain = Math.Min(remainingBudget, pendingEntries.Count);
                if (entriesToDrain == 0)
                {
                    continue;
                }

                for (int index = 0; index < entriesToDrain; index++)
                {
                    pendingEntries.Dequeue();
                }

                tabsToRefresh.Add(runtimeTab);
                if (pendingEntries.Count == 0)
                {
                    _pendingLogEntries.Remove(runtimeTab);
                }

                remainingBudget -= entriesToDrain;
            }

            hasMorePendingEntries = _pendingLogEntries.Count > 0;
        }

        return tabsToRefresh;
    }

    /// <summary>
    /// Refreshes the selected runtime-tab duration once per second while any runtime session remains active.
    /// </summary>
    private void HandleRuntimeDurationTimerTick(object? sender, EventArgs e)
    {
        /* The selected TIME pill and graph metric strips now come from live session task timings rather than from log
           activity. Refresh them once per second while any execution session remains active so long-running tasks keep
           counting up even during quiet periods with no new output. */
        foreach (RuntimeWorkspaceTabViewModel runtimeTab in RuntimeTabs.Where(tab => tab.Session != null))
        {
            runtimeTab.RefreshAllTaskMetrics();
        }

        RaisePropertyChanged(nameof(SelectedRuntimeMetrics));

        if (!RuntimeTabs.Any(tab => tab.IsRunning))
        {
            _runtimeDurationTimer.Stop();
        }
    }

    /// <summary>
    /// Mirrors selected runtime-tab property changes into the derived workspace properties used by the header.
    /// </summary>
    private void HandleSelectedRuntimeTabPropertyChanged(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            RaiseSelectionStateChanged();
            return;
        }

        switch (propertyName)
        {
            case nameof(RuntimeWorkspaceTabViewModel.DurationText):
                RaisePropertyChanged(nameof(SelectedRuntimeMetrics));
                break;
            default:
                RaiseSelectionStateChanged();
                break;
        }
    }

    /// <summary>
    /// Raises change notifications for all derived state shown by the runtime workspace.
    /// </summary>
    private void RaiseSelectionStateChanged()
    {
        RaisePropertyChanged(nameof(ExecutionSummary));
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(SelectedGraph));
        RaisePropertyChanged(nameof(SelectedTask));
        RaisePropertyChanged(nameof(SelectedRuntimeMetrics));
        RaisePropertyChanged(nameof(SelectedRuntimeLogEntries));
        RaisePropertyChanged(nameof(SelectedRuntimeLogSourceId));
        RaisePropertyChanged(nameof(SelectedRuntimeTabTitle));
        RaisePropertyChanged(nameof(ShowSelectedRuntimeMetrics));
    }

    /// <summary>
    /// Creates the permanent first workspace tab used for application-level logs.
    /// </summary>
    private static RuntimeWorkspaceTabViewModel CreateApplicationLogTab()
    {
        return new RuntimeWorkspaceTabViewModel(
            id: "application-log",
            title: "App Log",
            subtitle: string.Empty,
            kind: RuntimeWorkspaceTabKind.ApplicationLog,
            presentation: new RuntimeWorkspaceTabPresentation(showGraph: false, showLog: true, showSubtitle: false, showStatusMarker: false, showRuntimeMetrics: false),
            graph: new ExecutionGraphViewModel());
    }

    /// <summary>
    /// Creates the permanent plan-preview workspace tab.
    /// </summary>
    private static RuntimeWorkspaceTabViewModel CreatePlanPreviewTab()
    {
        RuntimeWorkspaceTabViewModel planTab = new(
            id: "plan-preview",
            title: "Plan",
            subtitle: "Current selection preview",
            kind: RuntimeWorkspaceTabKind.PlanPreview,
            presentation: new RuntimeWorkspaceTabPresentation(showGraph: true, showLog: false, showSubtitle: false, showStatusMarker: false, showRuntimeMetrics: false),
            graph: new ExecutionGraphViewModel());
        return planTab;
    }

    /// <summary>
    /// Adapts one shared log entry into the UI-friendly log row model.
    /// </summary>
    private static LogEntryViewModel CreateLogEntryViewModel(LogEntry entry)
    {
        return new LogEntryViewModel(entry.Message, entry.Verbosity, entry.Timestamp);
    }

}
