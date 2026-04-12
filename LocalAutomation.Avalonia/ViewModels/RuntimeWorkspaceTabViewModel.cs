using System;
using System.Collections.ObjectModel;
using System.Linq;
using LocalAutomation.Core;
using RuntimeExecutionSession = LocalAutomation.Runtime.ExecutionSession;
using RuntimeExecutionTask = LocalAutomation.Runtime.ExecutionTask;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;
using RuntimeExecutionTaskMetrics = LocalAutomation.Runtime.ExecutionTaskMetrics;
using RuntimeExecutionTaskOutcome = LocalAutomation.Runtime.ExecutionTaskOutcome;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Represents one execution-workspace tab, including the app log, the live plan preview, or a specific execution
/// session with graph state and scoped output.
/// </summary>
public sealed class RuntimeWorkspaceTabViewModel : ViewModelBase
{
    private bool _isSelected;
    private ObservableCollection<LogEntryViewModel> _selectedLogEntries = new();
    private readonly Dictionary<RuntimeExecutionTaskId, ExecutionTaskViewModel> _tasksById = new();

    /// <summary>
    /// Creates a workspace tab with the provided graph and optional execution session.
    /// </summary>
    public RuntimeWorkspaceTabViewModel(string id, string title, string subtitle, RuntimeWorkspaceTabKind kind, RuntimeWorkspaceTabPresentation presentation, ExecutionGraphViewModel graph, RuntimeExecutionSession? session = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Title = title ?? throw new ArgumentNullException(nameof(title));
        Subtitle = subtitle ?? throw new ArgumentNullException(nameof(subtitle));
        Kind = kind;
        Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
        Graph = graph ?? throw new ArgumentNullException(nameof(graph));
        Session = session;

        /* Every graph-rendering tab now shares task VMs through its tab-owned registry. Attach the dictionary once here
           so both execution-session tabs and the always-present plan-preview tab can materialize graph nodes safely. */
        Graph.AttachTasks(_tasksById);
    }

    /// <summary>
    /// Gets the stable tab identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the tab title shown in the strip.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the descriptive tab subtitle.
    /// </summary>
    public string Subtitle { get; }

    /// <summary>
    /// Gets the workspace-tab kind.
    /// </summary>
    public RuntimeWorkspaceTabKind Kind { get; }

    /// <summary>
    /// Gets the presentation profile used by the runtime workspace view.
    /// </summary>
    public RuntimeWorkspaceTabPresentation Presentation { get; }

    /// <summary>
    /// Gets the graph view model rendered inside the tab.
    /// </summary>
    public ExecutionGraphViewModel Graph { get; }

    /// <summary>
    /// Gets the backing execution session when this tab represents a live or completed run.
    /// </summary>
    public RuntimeExecutionSession? Session { get; }

    /// <summary>
    /// Gets the shared task view models for this tab, keyed by execution task id.
    /// </summary>
    public IReadOnlyDictionary<RuntimeExecutionTaskId, ExecutionTaskViewModel> TasksById => _tasksById;

    /// <summary>
    /// Gets the log entries currently shown in the details pane for the selected graph node or current tab-wide log view.
    /// </summary>
    public ObservableCollection<LogEntryViewModel> SelectedLogEntries
    {
        get => _selectedLogEntries;
        private set => SetProperty(ref _selectedLogEntries, value);
    }

    /// <summary>
    /// Gets or sets whether this tab is selected in the workspace strip.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Gets whether the tab should show a subtitle in the strip.
    /// </summary>
    public bool ShowsSubtitle => Presentation.ShowSubtitle && !string.IsNullOrWhiteSpace(Subtitle);

    /// <summary>
    /// Gets whether the tab can use the compact title-only strip layout.
    /// </summary>
    public bool UsesCompactStripLayout => !ShowsStatusMarker && !ShowsSubtitle && !CanClose;

    /// <summary>
    /// Gets whether the tab should use the richer strip layout with status and subtitle support.
    /// </summary>
    public bool UsesDetailedStripLayout => !UsesCompactStripLayout;

    /// <summary>
    /// Gets whether this tab is the permanent application log tab.
    /// </summary>
    public bool IsApplicationLog => Kind == RuntimeWorkspaceTabKind.ApplicationLog;

    /// <summary>
    /// Gets whether this tab is the permanent plan-preview tab.
    /// </summary>
    public bool IsPlanPreview => Kind == RuntimeWorkspaceTabKind.PlanPreview;

    /// <summary>
    /// Gets whether the tab can be closed by the user.
    /// </summary>
    public bool CanClose => Kind == RuntimeWorkspaceTabKind.ExecutionSession;

    /// <summary>
    /// Gets whether the tab represents a running execution session.
    /// </summary>
    public bool IsRunning => Session is { IsRunning: true };

    /// <summary>
    /// Gets whether the strip should render a status marker for this tab.
    /// </summary>
    public bool ShowsStatusMarker => Presentation.ShowStatusMarker;

    /// <summary>
    /// Gets whether this tab should render the execution graph pane.
    /// </summary>
    public bool ShowsGraph => Presentation.ShowGraph;

    /// <summary>
    /// Gets whether this tab should render the log pane.
    /// </summary>
    public bool ShowsLog => Presentation.ShowLog;

    /// <summary>
    /// Gets whether the selected-tab metrics should be shown for this tab.
    /// </summary>
    public bool ShowsRuntimeMetrics => Presentation.ShowRuntimeMetrics;

    /// <summary>
    /// Gets the semantic status rendered by the shared status-indicator control.
    /// </summary>
    public ExecutionTaskDisplayStatus SessionStatusForIndicator => Session?.IsRunning == true
        ? ExecutionTaskDisplayStatus.Running
        : Session?.Outcome == RuntimeExecutionTaskOutcome.Completed
            ? ExecutionTaskDisplayStatus.Completed
            : Session?.Outcome == RuntimeExecutionTaskOutcome.Failed
                ? ExecutionTaskDisplayStatus.Failed
                : Session?.Outcome == RuntimeExecutionTaskOutcome.Cancelled
                    ? ExecutionTaskDisplayStatus.Cancelled
                    : Session?.Outcome == RuntimeExecutionTaskOutcome.Interrupted
                        ? ExecutionTaskDisplayStatus.Interrupted
                    : ExecutionTaskDisplayStatus.Queued;

    /// <summary>
    /// Replaces the currently displayed log entries for the selected graph node or current tab-wide log view.
    /// </summary>
    public void SetSelectedLogEntries(System.Collections.Generic.IEnumerable<LogEntryViewModel> entries)
    {
        SelectedLogEntries = new ObservableCollection<LogEntryViewModel>(entries.ToList());
    }

    /// <summary>
    /// Replaces the shared task view-model registry for this execution tab from the current plan snapshot.
    /// </summary>
    public void SetTasks(IEnumerable<RuntimeExecutionTask> tasks)
    {
        /* Runtime graph refreshes often add only a few child tasks at a time. Reuse existing task view models for stable
           task ids so structural refreshes do not resubscribe every visible task and force avoidable UI churn. */
        List<RuntimeExecutionTask> materializedTasks = tasks?.ToList() ?? new List<RuntimeExecutionTask>();
        HashSet<RuntimeExecutionTaskId> incomingTaskIds = materializedTasks.Select(task => task.Id).ToHashSet();
        List<RuntimeExecutionTaskId> removedTaskIds = _tasksById.Keys
            .Where(taskId => !incomingTaskIds.Contains(taskId))
            .ToList();

        foreach (RuntimeExecutionTaskId removedTaskId in removedTaskIds)
        {
            _tasksById[removedTaskId].Dispose();
            _tasksById.Remove(removedTaskId);
        }

        foreach (RuntimeExecutionTask task in materializedTasks)
        {
            if (_tasksById.ContainsKey(task.Id))
            {
                continue;
            }

            _tasksById[task.Id] = new ExecutionTaskViewModel(task);
        }
    }

    /// <summary>
    /// Rebuilds all stored task metrics from the current runtime snapshot owned by the live execution tasks.
    /// </summary>
    public void RebuildMetricsFromSession(DateTimeOffset? now = null)
    {
        if (Session == null)
        {
            foreach (ExecutionTaskViewModel task in _tasksById.Values)
            {
                task.SetMetrics(RuntimeExecutionTaskMetrics.Empty);
            }

            return;
        }

        DateTimeOffset timestampNow = now ?? DateTimeOffset.Now;
        foreach (ExecutionTaskViewModel task in _tasksById.Values)
        {
            RuntimeExecutionTaskMetrics metrics = Session.GetTaskMetrics(task.Id, timestampNow);
            task.SetMetrics(metrics);
        }
    }

    /// <summary>
    /// Refreshes the stored runtime metrics for the specified tasks and their ancestors after runtime state or log
    /// changes.
    /// </summary>
    public void RefreshTaskMetrics(IEnumerable<RuntimeExecutionTaskId> taskIds, DateTimeOffset? now = null)
    {
        List<RuntimeExecutionTaskId> requestedTaskIds = taskIds?.ToList() ?? new List<RuntimeExecutionTaskId>();
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("RuntimeWorkspaceTab.RefreshTaskMetrics")
            .SetTag("tab.id", Id)
            .SetTag("tab.title", Title)
            .SetTag("requested.task.count", requestedTaskIds.Count)
            .SetTag("task.id", requestedTaskIds.Count > 0 ? requestedTaskIds[^1].Value : string.Empty);

        if (Session == null)
        {
            activity.SetTag("refresh.skipped", "MissingSession");
            return;
        }

        DateTimeOffset timestampNow = now ?? DateTimeOffset.Now;
        HashSet<RuntimeExecutionTaskId> refreshedTaskIds = new();
        foreach (RuntimeExecutionTaskId taskId in requestedTaskIds)
        {
            if (!_tasksById.TryGetValue(taskId, out ExecutionTaskViewModel? taskViewModel))
            {
                continue;
            }

            RuntimeExecutionTask? currentTask = taskViewModel.Task;
            while (currentTask != null && refreshedTaskIds.Add(currentTask.Id))
            {
                if (_tasksById.TryGetValue(currentTask.Id, out ExecutionTaskViewModel? currentTaskViewModel))
                {
                    currentTaskViewModel.SetMetrics(Session.GetTaskMetrics(currentTask.Id, timestampNow));
                }

                currentTask = currentTask.Parent;
            }
        }

        activity.SetTag("refreshed.task.count", refreshedTaskIds.Count);
    }

    /// <summary>
    /// Refreshes metrics for tasks whose subtree duration can still advance according to the runtime snapshot.
    /// </summary>
    public void RefreshLiveTaskDurations(DateTimeOffset? now = null)
    {
        RefreshTaskMetrics(
            _tasksById.Values
                .Where(task => task.Task.State == LocalAutomation.Runtime.ExecutionTaskState.Running)
                .Select(task => task.Id),
            now);
    }

    /// <summary>
    /// Disposes every shared task view model owned by this tab.
    /// </summary>
    public void DisposeTasks()
    {
        foreach (ExecutionTaskViewModel task in _tasksById.Values)
        {
            task.Dispose();
        }

        _tasksById.Clear();
    }

    /// <summary>
    /// Raises the tab properties that change when the backing execution session transitions between runtime states.
    /// </summary>
    public void NotifyStateChanged()
    {
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(SessionStatusForIndicator));
    }

}
