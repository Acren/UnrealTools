using System;
using System.Collections.ObjectModel;
using System.Linq;
using LocalAutomation.Core;
using ExecutionTaskStatus = LocalAutomation.Core.ExecutionTaskStatus;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Represents one execution-workspace tab, including the app log, the live plan preview, or a specific execution
/// session with graph state and scoped output.
/// </summary>
public sealed class RuntimeWorkspaceTabViewModel : ViewModelBase
{
    private bool _isSelected;
    private ObservableCollection<LogEntryViewModel> _selectedLogEntries = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionTaskViewModel> _tasksById = new();

    /// <summary>
    /// Creates a workspace tab with the provided graph and optional execution session.
    /// </summary>
    public RuntimeWorkspaceTabViewModel(string id, string title, string subtitle, RuntimeWorkspaceTabKind kind, RuntimeWorkspaceTabPresentation presentation, ExecutionGraphViewModel graph, ExecutionSession? session = null)
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
    public ExecutionSession? Session { get; }

    /// <summary>
    /// Gets the shared task view models for this tab, keyed by execution task id.
    /// </summary>
    public IReadOnlyDictionary<ExecutionTaskId, ExecutionTaskViewModel> TasksById => _tasksById;

    /// <summary>
    /// Gets the log entries currently shown in the details pane for the selected graph node or all-output pseudo-node.
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
    /// Gets whether this tab represents a started execution session.
    /// </summary>
    public bool IsExecutionSession => Kind == RuntimeWorkspaceTabKind.ExecutionSession;

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
    /// Gets whether the selected execution can be terminated from the header.
    /// </summary>
    public bool CanTerminate => Session is { IsRunning: true };

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
    /// Gets whether the status marker should show the running accent.
    /// </summary>
    public bool IsRunningStatus => Session?.IsRunning == true;

    /// <summary>
    /// Gets whether the status marker should show the success accent.
    /// </summary>
    public bool IsSucceededStatus => Session?.IsRunning != true && Session?.Outcome == RunOutcome.Succeeded;

    /// <summary>
    /// Gets whether the status marker should show the failure accent.
    /// </summary>
    public bool IsFailedStatus => Session?.IsRunning != true && Session?.Outcome == RunOutcome.Failed;

    /// <summary>
     /// Gets whether the status marker should show the cancelled accent.
     /// </summary>
    public bool IsCancelledStatus => Session?.IsRunning != true && Session?.Outcome == RunOutcome.Cancelled;

    /// <summary>
    /// Gets the semantic status rendered by the shared status-indicator control.
    /// </summary>
    public ExecutionTaskStatus SessionStatusForIndicator => Session?.IsRunning == true
        ? ExecutionTaskStatus.Running
        : Session?.Outcome == RunOutcome.Succeeded
            ? ExecutionTaskStatus.Completed
            : Session?.Outcome == RunOutcome.Failed
                ? ExecutionTaskStatus.Failed
                : Session?.Outcome == RunOutcome.Cancelled
                    ? ExecutionTaskStatus.Cancelled
                    : ExecutionTaskStatus.Pending;

    /// <summary>
    /// Gets the execution duration text shown in the selected-tab header.
    /// </summary>
    public string DurationText
    {
        get
        {
            if (Session == null)
            {
                return "--:--";
            }

            DateTimeOffset endTime = Session.FinishedAt ?? DateTimeOffset.Now;
            TimeSpan duration = endTime - Session.StartedAt;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"mm\:ss");
        }
    }

    /// <summary>
    /// Replaces the currently displayed log entries for the selected graph node or all-output view.
    /// </summary>
    public void SetSelectedLogEntries(System.Collections.Generic.IEnumerable<LogEntryViewModel> entries)
    {
        SelectedLogEntries = new ObservableCollection<LogEntryViewModel>(entries.ToList());
        RaiseSelectionMetricsChanged();
    }

    /// <summary>
    /// Replaces the shared task view-model registry for this execution tab from the current plan snapshot.
    /// </summary>
    public void SetTasks(IEnumerable<ExecutionPlanTask> tasks)
    {
        foreach (ExecutionTaskViewModel existingTask in _tasksById.Values)
        {
            existingTask.Dispose();
        }

        _tasksById.Clear();
        List<ExecutionPlanTask> materializedTasks = tasks?.ToList() ?? new List<ExecutionPlanTask>();
        Dictionary<ExecutionTaskId, ExecutionTaskId?> parentIds = materializedTasks.ToDictionary(task => task.Id, task => task.ParentId);
        foreach (ExecutionPlanTask task in materializedTasks)
        {
            HashSet<ExecutionTaskId> subtreeTaskIds = BuildSubtreeTaskIds(task.Id, parentIds);
            _tasksById[task.Id] = new ExecutionTaskViewModel(task, Session, Session?.GetTaskRuntimeState(task.Id), subtreeTaskIds);
        }
    }

    /// <summary>
    /// Returns the shared task view model for one execution task id when this tab currently knows about it.
    /// </summary>
    public ExecutionTaskViewModel? GetTask(ExecutionTaskId taskId)
    {
        return _tasksById.TryGetValue(taskId, out ExecutionTaskViewModel? task) ? task : null;
    }

    /// <summary>
     /// Refreshes all shared task metrics from the current session snapshot.
     /// </summary>
    public void RefreshAllTaskMetrics()
    {
        foreach (ExecutionTaskViewModel task in _tasksById.Values)
        {
            task.RefreshTimeSensitiveState();
        }
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
    /// Builds the full descendant-inclusive subtree id set for one task from the current plan hierarchy.
    /// </summary>
    private static HashSet<ExecutionTaskId> BuildSubtreeTaskIds(ExecutionTaskId rootTaskId, IReadOnlyDictionary<ExecutionTaskId, ExecutionTaskId?> parentIds)
    {
        HashSet<ExecutionTaskId> subtreeTaskIds = new() { rootTaskId };
        foreach ((ExecutionTaskId taskId, ExecutionTaskId? parentId) in parentIds)
        {
            if (taskId == rootTaskId)
            {
                continue;
            }

            ExecutionTaskId? currentParentId = parentId;
            while (currentParentId != null)
            {
                if (currentParentId.Value == rootTaskId)
                {
                    subtreeTaskIds.Add(taskId);
                    break;
                }

                currentParentId = parentIds.TryGetValue(currentParentId.Value, out ExecutionTaskId? nextParentId)
                    ? nextParentId
                    : null;
            }
        }

        return subtreeTaskIds;
    }

    /// <summary>
    /// Raises derived state after execution or selection data changes.
    /// </summary>
    public void NotifyStateChanged()
    {
        RaisePropertyChanged(nameof(DurationText));
        RaisePropertyChanged(nameof(IsRunning));
        RaisePropertyChanged(nameof(CanTerminate));
        RaiseStatusChanged();
    }

    /// <summary>
    /// Raises selection-sensitive derived properties that still depend on the currently displayed log stream.
    /// </summary>
    private void RaiseSelectionMetricsChanged()
    {
        RaisePropertyChanged(nameof(DurationText));
    }

    /// <summary>
    /// Raises the tab-strip status marker properties.
    /// </summary>
    private void RaiseStatusChanged()
    {
        RaisePropertyChanged(nameof(ShowsStatusMarker));
        RaisePropertyChanged(nameof(IsRunningStatus));
        RaisePropertyChanged(nameof(IsSucceededStatus));
        RaisePropertyChanged(nameof(IsFailedStatus));
        RaisePropertyChanged(nameof(IsCancelledStatus));
        RaisePropertyChanged(nameof(SessionStatusForIndicator));
    }
}
