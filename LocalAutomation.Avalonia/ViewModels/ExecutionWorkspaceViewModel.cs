using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;
using RuntimeExecutionPlan = LocalAutomation.Runtime.ExecutionPlan;
using RuntimeExecutionSession = LocalAutomation.Runtime.ExecutionSession;
using RuntimeExecutionSessionId = LocalAutomation.Runtime.ExecutionSessionId;
using RuntimeExecutionTask = LocalAutomation.Runtime.ExecutionTask;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;
using RuntimeExecutionTaskMetrics = LocalAutomation.Runtime.ExecutionTaskMetrics;
using RuntimeExecutionTaskOutcome = LocalAutomation.Runtime.ExecutionTaskOutcome;
using RuntimeExecutionTaskState = LocalAutomation.Runtime.ExecutionTaskState;

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
    private const int DispatcherInstrumentationInterval = 250;
    private const int SelectionInstrumentationInterval = 100;
    private const int LogRebuildInstrumentationInterval = 25;

    private readonly LocalAutomationApplicationHost _services;
    private readonly object _pendingLogSyncRoot = new();
    private readonly object _pendingTaskStateSyncRoot = new();
    private readonly object _pendingGraphRefreshSyncRoot = new();
    private readonly Action<string> _setStatus;
    private readonly DispatcherTimer _pendingLogFlushTimer;
    private readonly DispatcherTimer _runtimeDurationTimer;
    private readonly Dictionary<RuntimeWorkspaceTabViewModel, Queue<LogEntryViewModel>> _pendingLogEntries = new();
    private readonly Dictionary<RuntimeWorkspaceTabViewModel, HashSet<RuntimeExecutionTaskId>> _pendingTaskStateChanges = new();
    private readonly HashSet<RuntimeWorkspaceTabViewModel> _taskStateFlushQueuedTabs = new();
    private readonly HashSet<RuntimeWorkspaceTabViewModel> _pendingGraphRefreshTabs = new();
    private readonly Dictionary<RuntimeWorkspaceTabViewModel, ILogStream> _attachedLogStreams = new();
    private readonly Dictionary<RuntimeWorkspaceTabViewModel, RuntimeExecutionSession> _attachedSessions = new();
    private int _taskStatusDispatcherPostCount;
    private int _selectionStateChangeCount;
    private int _selectedLogRebuildCount;
    private bool _isPendingLogFlushStartQueued;
    private RuntimeWorkspaceTabViewModel? _observedWorkspaceTab;
    private RuntimeWorkspaceTabViewModel? _selectedRuntimeTab;

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
        _services.ApplicationSettings.PropertyChanged += HandleApplicationSettingsChanged;

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
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionWorkspace.SetSelectedRuntimeTab")
                .SetTag("previous.tab.id", _selectedRuntimeTab?.Id ?? string.Empty)
                .SetTag("previous.tab.kind", _selectedRuntimeTab?.Kind.ToString() ?? string.Empty)
                .SetTag("next.tab.id", value?.Id ?? string.Empty)
                .SetTag("next.tab.kind", value?.Kind.ToString() ?? string.Empty);

            /* The shared graph host should only see a SelectedGraph property change when the selected tab actually swaps
               to a different graph owner. Broadly re-raising SelectedGraph during unrelated header/log refreshes causes
               the canvas DataContext to churn and forces a full measured redraw. */
            ExecutionGraphViewModel? previousSelectedGraph = SelectedGraph;
            if (!SetProperty(ref _selectedRuntimeTab, value))
            {
                activity.SetTag("selection.changed", false);
                return;
            }

            activity.SetTag("selection.changed", true);

            if (_observedWorkspaceTab != null)
            {
                _observedWorkspaceTab.PropertyChanged -= HandleObservedRuntimeTabPropertyChanged;
                _observedWorkspaceTab = null;
            }

            if (value != null)
            {
                value.PropertyChanged += HandleObservedRuntimeTabPropertyChanged;
                _observedWorkspaceTab = value;
            }

            foreach (RuntimeWorkspaceTabViewModel tab in RuntimeTabs)
            {
                tab.IsSelected = ReferenceEquals(tab, value);
            }

            if (value != null)
            {
                RebuildTabSelectedLogEntries(value);
                // The buffered log streams remain the source of truth even while a tab is hidden. We can discard any
                // queued UI rows for non-selected tabs because selecting a tab always rebuilds the visible pane from the
                // full buffered stream instead of relying on incremental rows that happened to stay queued.
                foreach (RuntimeWorkspaceTabViewModel tab in RuntimeTabs.Where(tab => !ReferenceEquals(tab, value) && _attachedLogStreams.ContainsKey(tab)).ToList())
                {
                    RemovePendingLogEntries(tab);
                }
            }

            if (!ReferenceEquals(previousSelectedGraph, SelectedGraph))
            {
                activity.SetTag("selected_graph.changed", true)
                    .SetTag("previous.graph.hash", previousSelectedGraph?.GetHashCode() ?? 0)
                    .SetTag("next.graph.hash", SelectedGraph?.GetHashCode() ?? 0);
                RaisePropertyChanged(nameof(SelectedGraph));
            }
            else
            {
                activity.SetTag("selected_graph.changed", false);
            }

            RaiseSelectionStateChanged();
        }
    }

    /// <summary>
    /// Gets whether the selected runtime tab currently represents active work that can be terminated.
    /// </summary>
    public bool IsRunning => SelectedRuntimeTab?.IsRunning == true;

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
    /// Gets whether the selected runtime tab should show task metrics in the header.
    /// </summary>
    public bool ShowSelectedRuntimeMetrics => SelectedRuntimeTab?.ShowsRuntimeMetrics == true;

    /// <summary>
    /// Gets the shared metrics shown in the selected runtime-tab header.
    /// </summary>
    public RuntimeExecutionTaskMetrics SelectedRuntimeMetrics
    {
        get
        {
            RuntimeWorkspaceTabViewModel? selectedTab = SelectedRuntimeTab;
            if (selectedTab?.Session is not RuntimeExecutionSession session)
            {
                return RuntimeExecutionTaskMetrics.Empty;
            }

            return selectedTab.Graph.SelectedNode?.Task?.Metrics ?? session.GetTaskMetrics(selectedTab.Graph.SelectedTaskId);
        }
    }

    /// <summary>
    /// Gets the graph view model for the currently selected workspace tab when that tab renders a graph pane.
    /// </summary>
    public ExecutionGraphViewModel? SelectedGraph => SelectedRuntimeTab?.ShowsGraph == true ? SelectedRuntimeTab.Graph : null;

    /// <summary>
    /// Refreshes the permanent plan-preview tab from the latest selected target, operation, and option values.
    /// </summary>
    public void UpdatePlanPreview(RuntimeExecutionPlan? plan)
    {
        /* Trace the workspace-level preview update separately from shell refresh so the timing tree shows whether the
           cost sits in the plan graph rebuild or elsewhere in the surrounding view-model refresh. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("UpdatePlanPreview")
            .SetTag("plan.has_result", plan != null)
            .SetTag("plan.task.count", plan?.Tasks.Count ?? 0);

        RuntimeWorkspaceTabViewModel planTab = RuntimeTabs.First(tab => tab.Kind == RuntimeWorkspaceTabKind.PlanPreview);
        planTab.SetTasks(plan?.Tasks ?? Array.Empty<RuntimeExecutionTask>());
        planTab.Graph.SetGraph(plan?.Tasks);
        RebuildTabSelectedLogEntries(planTab);

        if (ReferenceEquals(SelectedRuntimeTab, planTab))
        {
            RaiseSelectedGraphSelectionStateChanged();
        }
    }

    /// <summary>
    /// Adds a newly started execution session to the workspace and begins mirroring its plan, task states, and logs.
    /// </summary>
    public void AttachExecutionSession(RuntimeExecutionSession session)
    {
        if (session == null)
        {
            throw new ArgumentNullException(nameof(session));
        }

        ExecutionGraphViewModel graph = CreateExecutionGraphViewModel();
        RuntimeWorkspaceTabViewModel runtimeTab = new(
            id: session.Id.Value,
            title: session.OperationName,
            subtitle: session.TargetName,
            kind: RuntimeWorkspaceTabKind.ExecutionSession,
            presentation: new RuntimeWorkspaceTabPresentation(showGraph: true, showLog: true, showSubtitle: true, showStatusMarker: true, showRuntimeMetrics: true),
            graph: graph,
            session: session);
        /* Runtime child-task insertion can mutate the live session graph between reads, so build the shared task-view
           registry and the rendered graph from the same materialized snapshot each time. */
        List<RuntimeExecutionTask> taskSnapshot = session.Tasks.ToList();
        runtimeTab.SetTasks(taskSnapshot);
        /* The selected plan-preview tab has usually just measured the same authored graph. Reuse those widths when the
           execution session tab opens so the first runtime render can skip a redundant full hidden-host measurement pass
           for unchanged nodes. */
        ExecutionGraphViewModel? selectedGraph = SelectedGraph;
        if (selectedGraph != null && !ReferenceEquals(selectedGraph, graph))
        {
            graph.ImportMeasuredNodeWidthsFrom(selectedGraph, taskSnapshot);
        }

        graph.SetGraph(taskSnapshot);

        HookGraphSelection(runtimeTab);
        RuntimeTabs.Add(runtimeTab);
        SelectedRuntimeTab = runtimeTab;
        if (!_runtimeDurationTimer.IsEnabled)
        {
            _runtimeDurationTimer.Start();
        }

        AttachSessionLogs(runtimeTab, session);
        session.TaskStateChanged += (taskId, state, outcome, statusReason) =>
        {
            EnqueuePendingTaskStateChange(runtimeTab, taskId, state, outcome, statusReason);
        };

        /* Child-task insertion can arrive in bursts while the runtime is expanding nested work. Queue at most one
           structural graph refresh per tab so the UI thread does not rebuild the full graph repeatedly before the first
           posted refresh has even run. */
        session.TaskGraphChanged += () => EnqueuePendingGraphRefresh(runtimeTab);

        _attachedSessions[runtimeTab] = session;
        _ = WatchExecutionCompletionAsync(runtimeTab);
    }

    /// <summary>
    /// Rebuilds one execution-session tab from the current live session task graph after runtime child attachment adds
    /// new descendants beneath an existing task.
    /// </summary>
    private void RefreshRuntimeSessionGraph(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        RuntimeExecutionSession? session = runtimeTab.Session;
        if (session == null)
        {
            return;
        }

        /* Graph refresh must reuse one consistent session snapshot so task view-model lookup and graph-node creation
           cannot diverge while runtime child tasks are being merged into the live session. */
        List<RuntimeExecutionTask> taskSnapshot = session.Tasks.ToList();
        runtimeTab.SetTasks(taskSnapshot);
        runtimeTab.Graph.SetGraph(taskSnapshot);
        RebuildTabSelectedLogEntries(runtimeTab);
        runtimeTab.RefreshAllTaskMetrics();
        if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
        {
            RaiseSelectedGraphSelectionStateChanged();
        }
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
        RaiseSelectedRuntimeHeaderStateChanged();
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
            RaiseSelectedRuntimeLogStateChanged();
            return;
        }

        if (SelectedRuntimeTab.Session != null)
        {
            RuntimeExecutionTaskId? selectedTaskId = SelectedRuntimeTab.Graph.SelectedTaskId;
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
        _setStatus($"Cleared log output for {SelectedRuntimeTab.Title.ToLowerInvariant()}.");
        RaiseSelectedRuntimeLogStateChanged();
        RaisePropertyChanged(nameof(SelectedRuntimeMetrics));
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
            RaiseSelectedGraphSelectionStateChanged();
        }
    }

    /// <summary>
    /// Awaits the runtime-owned completion signal so completion state can be reflected back into the workspace.
    /// </summary>
    private async Task WatchExecutionCompletionAsync(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        RuntimeExecutionSession session = runtimeTab.Session!;
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionWorkspace.WatchExecutionCompletion")
            .SetTag("operation.name", session.OperationName)
            .SetTag("target.name", session.TargetName);
        await session.Completion.ConfigureAwait(false);

        activity.SetTag("session.outcome", session.Outcome.ToString());
        DateTime uiDispatchRequestedAtUtc = DateTime.UtcNow;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using PerformanceActivityScope uiActivity = PerformanceTelemetry.StartActivity("ExecutionWorkspace.ApplyCompletionState")
                .SetTag("operation.name", session.OperationName)
                .SetTag("target.name", session.TargetName)
                .SetTag("session.outcome", session.Outcome.ToString())
                .SetTag("ui.dispatch.delay_ms", (DateTime.UtcNow - uiDispatchRequestedAtUtc).TotalMilliseconds.ToString("0"));
            runtimeTab.RefreshAllTaskMetrics();
            runtimeTab.NotifyStateChanged();
            if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                RaiseSelectedRuntimeHeaderStateChanged();
            }

            if (session.Outcome == RuntimeExecutionTaskOutcome.Completed)
            {
                _setStatus($"{session.OperationName} succeeded for {session.TargetName}.");
            }
            else if (session.Outcome == RuntimeExecutionTaskOutcome.Cancelled)
            {
                _setStatus($"{session.OperationName} was cancelled for {session.TargetName}.");
            }
            else if (session.Outcome == RuntimeExecutionTaskOutcome.Interrupted)
            {
                _setStatus($"{session.OperationName} was interrupted for {session.TargetName}.");
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
        RemovePendingTaskStateChanges(runtimeTab);
        RemovePendingGraphRefresh(runtimeTab);
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

    }

    /// <summary>
    /// Buffers task-state changes per runtime tab so one burst of scheduler transitions turns into one dispatcher flush
    /// instead of hundreds of tiny UI callbacks that fight with graph rendering for the UI thread.
    /// </summary>
    private void EnqueuePendingTaskStateChange(RuntimeWorkspaceTabViewModel runtimeTab, RuntimeExecutionTaskId taskId, RuntimeExecutionTaskState state, RuntimeExecutionTaskOutcome? outcome, string? statusReason)
    {
        bool shouldPostFlush;
        DateTime postedAtUtc = DateTime.UtcNow;
        lock (_pendingTaskStateSyncRoot)
        {
            if (!_pendingTaskStateChanges.TryGetValue(runtimeTab, out HashSet<RuntimeExecutionTaskId>? pendingTaskIds))
            {
                pendingTaskIds = new HashSet<RuntimeExecutionTaskId>();
                _pendingTaskStateChanges[runtimeTab] = pendingTaskIds;
            }

            pendingTaskIds.Add(taskId);
            shouldPostFlush = _taskStateFlushQueuedTabs.Add(runtimeTab);
        }

        if (!shouldPostFlush)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => FlushPendingTaskStateChanges(runtimeTab, postedAtUtc, state, outcome, statusReason));
    }

    /// <summary>
    /// Applies one queued batch of task-state changes for a runtime tab on the UI thread.
    /// </summary>
    private void FlushPendingTaskStateChanges(RuntimeWorkspaceTabViewModel runtimeTab, DateTime postedAtUtc, RuntimeExecutionTaskState latestState, RuntimeExecutionTaskOutcome? latestOutcome, string? latestStatusReason)
    {
        List<RuntimeExecutionTaskId> pendingTaskIds;
        lock (_pendingTaskStateSyncRoot)
        {
            _taskStateFlushQueuedTabs.Remove(runtimeTab);
            if (!_pendingTaskStateChanges.Remove(runtimeTab, out HashSet<RuntimeExecutionTaskId>? pendingTaskIdSet) || pendingTaskIdSet.Count == 0)
            {
                return;
            }

            pendingTaskIds = pendingTaskIdSet.ToList();
        }

        if (!_attachedSessions.ContainsKey(runtimeTab))
        {
            return;
        }

        int postSequence = Interlocked.Increment(ref _taskStatusDispatcherPostCount);
        using PerformanceActivityScope uiActivity = CreateDispatcherPostedActivity(
            "ExecutionWorkspace.TaskStateChanged.Dispatch",
            runtimeTab,
            postedAtUtc,
            postSequence,
            extraTags: activity => activity
                .SetTag("task.batch.count", pendingTaskIds.Count.ToString())
                .SetTag("task.id", pendingTaskIds[^1].Value)
                .SetTag("task.state", latestState.ToString())
                .SetTag("task.outcome", latestOutcome?.ToString() ?? string.Empty)
                .SetTag("task.status_reason", latestStatusReason ?? string.Empty));

        /* Task view models already react to the underlying runtime model changes. This flush only updates graph-level
           selection visuals and one shared metrics/header pass, so one burst of runtime transitions costs one UI update. */
        foreach (RuntimeExecutionTaskId pendingTaskId in pendingTaskIds)
        {
            runtimeTab.Graph.NotifyTaskStateChanged(pendingTaskId);
        }

        runtimeTab.RefreshAllTaskMetrics();
        if (ReferenceEquals(SelectedRuntimeTab, runtimeTab))
        {
            RaisePropertyChanged(nameof(SelectedRuntimeMetrics));
        }
    }

    /// <summary>
    /// Drops any queued task-state flush for a runtime tab that is being cleared or removed.
    /// </summary>
    private void RemovePendingTaskStateChanges(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        lock (_pendingTaskStateSyncRoot)
        {
            _pendingTaskStateChanges.Remove(runtimeTab);
            _taskStateFlushQueuedTabs.Remove(runtimeTab);
        }
    }

    /// <summary>
    /// Queues one structural graph refresh for the provided runtime tab so bursts of child-task insertion collapse into
    /// one UI-thread rebuild instead of posting one dispatcher callback per graph mutation.
    /// </summary>
    private void EnqueuePendingGraphRefresh(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        bool shouldPostRefresh;
        lock (_pendingGraphRefreshSyncRoot)
        {
            shouldPostRefresh = _pendingGraphRefreshTabs.Add(runtimeTab);
        }

        if (!shouldPostRefresh)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_pendingGraphRefreshSyncRoot)
            {
                if (!_pendingGraphRefreshTabs.Remove(runtimeTab))
                {
                    return;
                }
            }

            /* Closing a tab removes its session before the posted callback runs. Re-check that the tab still owns a live
               attached session so queued refreshes quietly disappear instead of rebuilding a tab that no longer exists. */
            if (!_attachedSessions.ContainsKey(runtimeTab))
            {
                return;
            }

            RefreshRuntimeSessionGraph(runtimeTab);
        });
    }

    /// <summary>
    /// Drops any queued structural graph refresh for a runtime tab that is being torn down.
    /// </summary>
    private void RemovePendingGraphRefresh(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        lock (_pendingGraphRefreshSyncRoot)
        {
            _pendingGraphRefreshTabs.Remove(runtimeTab);
        }
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
    private void AttachSessionLogs(RuntimeWorkspaceTabViewModel runtimeTab, RuntimeExecutionSession session)
    {
        AttachLogStream(runtimeTab, session.LogStream);
    }

    /// <summary>
    /// Attaches one log stream to a workspace tab and seeds the currently visible log pane from its buffered entries.
    /// </summary>
    private void AttachLogStream(RuntimeWorkspaceTabViewModel runtimeTab, ILogStream logStream)
    {
        _attachedLogStreams[runtimeTab] = logStream;
        RebuildTabSelectedLogEntries(runtimeTab);
        // Always capture log entries regardless of tab visibility. Visibility only controls when we rebuild a tab's
        // visible pane, never whether entries are collected into that tab's buffered source stream.
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
                RaiseSelectedGraphSelectionStateChanged();
            }
        };
    }

    /// <summary>
    /// Rebuilds the selected log pane contents for one workspace tab from the appropriate aggregate or task-scoped log
    /// stream.
    /// </summary>
    private void RebuildTabSelectedLogEntries(RuntimeWorkspaceTabViewModel runtimeTab)
    {
        int rebuildSequence = Interlocked.Increment(ref _selectedLogRebuildCount);
        using PerformanceActivityScope activity = rebuildSequence % LogRebuildInstrumentationInterval == 0
            ? PerformanceTelemetry.StartActivity("ExecutionWorkspace.RebuildSelectedLogEntries")
                .SetTag("tab.id", runtimeTab.Id)
                .SetTag("tab.title", runtimeTab.Title)
                .SetTag("tab.kind", runtimeTab.Kind.ToString())
                .SetTag("rebuild.sequence", rebuildSequence)
            : default;

        if (runtimeTab.IsApplicationLog)
        {
            List<LogEntryViewModel> applicationEntries = ApplicationLogService.LogStream.Entries.Select(CreateLogEntryViewModel).ToList();
            activity.SetTag("log.source", "application")
                .SetTag("selected.task.count", 0)
                .SetTag("session.entry.count", applicationEntries.Count)
                .SetTag("visible.entry.count", applicationEntries.Count);
            runtimeTab.SetSelectedLogEntries(applicationEntries);
            return;
        }

        if (runtimeTab.Session == null)
        {
            activity.SetTag("log.source", "none")
                .SetTag("selected.task.count", 0)
                .SetTag("session.entry.count", 0)
                .SetTag("visible.entry.count", 0);
            runtimeTab.SetSelectedLogEntries(Array.Empty<LogEntryViewModel>());
            return;
        }

        IReadOnlyList<RuntimeExecutionTaskId> selectedTaskIds = runtimeTab.Graph.GetSelectedLogTaskIds();
        int sessionEntryCount = runtimeTab.Session.LogStream.Entries.Count;
        if (selectedTaskIds.Count == 0)
        {
            List<LogEntryViewModel> sessionEntries = runtimeTab.Session.LogStream.Entries.Select(CreateLogEntryViewModel).ToList();
            activity.SetTag("log.source", "session")
                .SetTag("selected.task.count", 0)
                .SetTag("session.entry.count", sessionEntryCount)
                .SetTag("visible.entry.count", sessionEntries.Count);
            runtimeTab.SetSelectedLogEntries(sessionEntries);
            return;
        }

        HashSet<RuntimeExecutionTaskId> selectedTaskIdSet = new(selectedTaskIds);
        List<LogEntryViewModel> filteredEntries = runtimeTab.Session.LogStream.Entries
            .Where(entry => RuntimeExecutionTaskId.FromNullable(entry.TaskId) is RuntimeExecutionTaskId taskId && selectedTaskIdSet.Contains(taskId))
            .Select(CreateLogEntryViewModel)
            .ToList();
        activity.SetTag("log.source", "selection")
            .SetTag("selected.task.count", selectedTaskIdSet.Count)
            .SetTag("session.entry.count", sessionEntryCount)
            .SetTag("visible.entry.count", filteredEntries.Count);
        runtimeTab.SetSelectedLogEntries(filteredEntries);
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
            // Hidden tabs still collect entries, but only the selected tab pays the cost of rebuilding its visible
            // pane during live streaming. When a hidden tab becomes selected, the pane is rebuilt from the buffered
            // stream so no captured output depends on prior visibility.
            if (!ReferenceEquals(SelectedRuntimeTab, runtimeTab))
            {
                continue;
            }

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
    /// Creates one activity for a dispatcher-posted callback and records how long the work sat in the UI queue before it ran.
    /// </summary>
    private static PerformanceActivityScope CreateDispatcherPostedActivity(string activityName, RuntimeWorkspaceTabViewModel runtimeTab, DateTime postedAtUtc, int postSequence, Func<PerformanceActivityScope, PerformanceActivityScope>? extraTags = null)
    {
        if (postSequence % DispatcherInstrumentationInterval != 0)
        {
            return default;
        }

        PerformanceActivityScope activity = PerformanceTelemetry.StartActivity(activityName)
            .SetTag("tab.id", runtimeTab.Id)
            .SetTag("tab.title", runtimeTab.Title)
            .SetTag("dispatch.post.sequence", postSequence)
            .SetTag("dispatch.queue.delay_ms", (DateTime.UtcNow - postedAtUtc).TotalMilliseconds.ToString("0"));
        return extraTags != null ? extraTags(activity) : activity;
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
    /// Forwards the currently selected tab's property changes into the workspace-level derived-property invalidation path.
    /// </summary>
    private void HandleObservedRuntimeTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        HandleSelectedRuntimeTabPropertyChanged(e.PropertyName);
    }

    /// <summary>
    /// Mirrors selected runtime-tab property changes into the derived workspace properties used by the active bindings.
    /// </summary>
    private void HandleSelectedRuntimeTabPropertyChanged(string? propertyName)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionWorkspace.HandleSelectedRuntimeTabPropertyChanged")
            .SetTag("property.name", propertyName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            activity.SetTag("action", "RaiseSelectionStateChanged");
            RaiseSelectionStateChanged();
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeWorkspaceTabViewModel.SelectedLogEntries), StringComparison.Ordinal))
        {
            activity.SetTag("action", "RaiseSelectedRuntimeLogEntries");
            RaiseSelectedRuntimeLogStateChanged();
            return;
        }

        if (string.Equals(propertyName, nameof(RuntimeWorkspaceTabViewModel.IsRunning), StringComparison.Ordinal))
        {
            activity.SetTag("action", "RaiseSelectedRuntimeHeaderState");
            RaiseSelectedRuntimeHeaderStateChanged();
            return;
        }

        activity.SetTag("action", "NoWorkspaceDerivedChange");
    }

    /// <summary>
    /// Raises change notifications for all derived state shown by the runtime workspace.
    /// </summary>
    private void RaiseSelectionStateChanged()
    {
        int selectionSequence = Interlocked.Increment(ref _selectionStateChangeCount);
        using PerformanceActivityScope activity = selectionSequence % SelectionInstrumentationInterval == 0
            ? PerformanceTelemetry.StartActivity("ExecutionWorkspace.RaiseSelectionStateChanged")
                .SetTag("sequence", selectionSequence)
                .SetTag("selected.tab.id", SelectedRuntimeTab?.Id ?? string.Empty)
                .SetTag("selected.tab.kind", SelectedRuntimeTab?.Kind.ToString() ?? string.Empty)
                .SetTag("selected.tab.shows_graph", SelectedRuntimeTab?.ShowsGraph ?? false)
                .SetTag("selected.tab.shows_log", SelectedRuntimeTab?.ShowsLog ?? false)
                .SetTag("selected.tab.shows_metrics", SelectedRuntimeTab?.ShowsRuntimeMetrics ?? false)
            : default;

        RaiseWorkspaceProperties(
            "ExecutionWorkspace.RaiseSelectionStateChanged.Properties",
            nameof(IsRunning),
            nameof(SelectedRuntimeMetrics),
            nameof(SelectedRuntimeLogEntries),
            nameof(SelectedRuntimeLogSourceId),
            nameof(ShowSelectedRuntimeMetrics));
    }

    /// <summary>
    /// Raises one targeted set of workspace properties and records which derived values were invalidated.
    /// </summary>
    private void RaiseWorkspaceProperties(string activityName, params string[] propertyNames)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity(activityName)
            .SetTag("property.count", propertyNames.Length)
            .SetTag("property.names", string.Join(",", propertyNames));
        foreach (string propertyName in propertyNames)
        {
            RaisePropertyChanged(propertyName);
        }
    }

    /// <summary>
    /// Raises the selected-tab header properties whose values depend on the selected runtime tab and its session state.
    /// </summary>
    private void RaiseSelectedRuntimeHeaderStateChanged()
    {
        RaiseWorkspaceProperties(
            "ExecutionWorkspace.RaiseSelectedRuntimeHeaderStateChanged",
            nameof(IsRunning),
            nameof(SelectedRuntimeMetrics));
    }

    /// <summary>
    /// Raises the selected log pane entries without invalidating unrelated header or selection state.
    /// </summary>
    private void RaiseSelectedRuntimeLogStateChanged()
    {
        RaiseWorkspaceProperties(
            "ExecutionWorkspace.RaiseSelectedRuntimeLogStateChanged",
            nameof(SelectedRuntimeLogEntries));
    }

    /// <summary>
    /// Raises the selected graph-driven properties after node selection or graph structure changes affect the scoped log
    /// stream or selected-task metrics.
    /// </summary>
    private void RaiseSelectedGraphSelectionStateChanged()
    {
        RaiseWorkspaceProperties(
            "ExecutionWorkspace.RaiseSelectedGraphSelectionStateChanged",
            nameof(SelectedRuntimeMetrics),
            nameof(SelectedRuntimeLogEntries),
            nameof(SelectedRuntimeLogSourceId));
    }

    /// <summary>
    /// Creates the permanent first workspace tab used for application-level logs.
    /// </summary>
    private RuntimeWorkspaceTabViewModel CreateApplicationLogTab()
    {
        return new RuntimeWorkspaceTabViewModel(
            id: "application-log",
            title: "App Log",
            subtitle: string.Empty,
            kind: RuntimeWorkspaceTabKind.ApplicationLog,
            presentation: new RuntimeWorkspaceTabPresentation(showGraph: false, showLog: true, showSubtitle: false, showStatusMarker: false, showRuntimeMetrics: false),
            graph: CreateExecutionGraphViewModel());
    }

    /// <summary>
    /// Creates the permanent plan-preview workspace tab.
    /// </summary>
    private RuntimeWorkspaceTabViewModel CreatePlanPreviewTab()
    {
        RuntimeWorkspaceTabViewModel planTab = new(
            id: "plan-preview",
            title: "Plan",
            subtitle: "Current selection preview",
            kind: RuntimeWorkspaceTabKind.PlanPreview,
            presentation: new RuntimeWorkspaceTabPresentation(showGraph: true, showLog: false, showSubtitle: false, showStatusMarker: false, showRuntimeMetrics: false),
            graph: CreateExecutionGraphViewModel());
        return planTab;
    }

    /// <summary>
    /// Creates one graph view model that starts with the current global hidden-task reveal preference.
    /// </summary>
    private ExecutionGraphViewModel CreateExecutionGraphViewModel()
    {
        ExecutionGraphViewModel graph = new();
        graph.SetRevealHiddenTasks(_services.ApplicationSettings.RevealHiddenTasks);
        return graph;
    }

    /// <summary>
    /// Refreshes all open graph tabs when the global hidden-task reveal preference changes.
    /// </summary>
    private void HandleApplicationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.PropertyName) &&
            !string.Equals(e.PropertyName, nameof(LocalAutomation.Application.ApplicationSettings.RevealHiddenTasks), StringComparison.Ordinal))
        {
            return;
        }

        bool revealHiddenTasks = _services.ApplicationSettings.RevealHiddenTasks;
        foreach (RuntimeWorkspaceTabViewModel tab in RuntimeTabs)
        {
            tab.Graph.SetRevealHiddenTasks(revealHiddenTasks);
            if (tab.ShowsLog)
            {
                RebuildTabSelectedLogEntries(tab);
            }
        }

        RaiseSelectionStateChanged();
    }

    /// <summary>
    /// Adapts one shared log entry into the UI-friendly log row model.
    /// </summary>
    private static LogEntryViewModel CreateLogEntryViewModel(LogEntry entry)
    {
        return new LogEntryViewModel(entry.Message, entry.Verbosity, entry.Timestamp);
    }

}
