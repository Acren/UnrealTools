using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LocalAutomation.Avalonia.Collections;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Core;
using RuntimeExecutionTask = LocalAutomation.Runtime.ExecutionTask;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Coordinates the execution-graph projection, bindable node and edge view models, selection state, and relayout
/// triggers for the runtime workspace while delegating structural projection and geometry calculation to the dedicated
/// graph layout layer.
/// </summary>
public sealed class ExecutionGraphViewModel : ViewModelBase
{
    private readonly RangeObservableCollection<ExecutionNodeViewModel> _nodes = new();
    private readonly RangeObservableCollection<ExecutionEdgeViewModel> _edges = new();
    private readonly Dictionary<RuntimeExecutionTaskId, ExecutionNodeViewModel> _nodesById = new();
    private readonly ExecutionGraphLayoutState _layoutState = new();
    private IReadOnlyDictionary<RuntimeExecutionTaskId, ExecutionTaskViewModel> _tasksById = new Dictionary<RuntimeExecutionTaskId, ExecutionTaskViewModel>();
    private IReadOnlyList<RuntimeExecutionTask>? _sourceTasks;
    private ExecutionGraphProjection _projection = ExecutionGraphProjection.Empty;
    private ExecutionGraphLayoutResult _layoutResult = ExecutionGraphLayoutResult.Empty;
    private ExecutionNodeViewModel? _selectedNode;
    private double _canvasWidth = ExecutionGraphLayoutSettings.NodeMinWidth;
    private double _canvasHeight = ExecutionGraphLayoutSettings.NodeHeight;
    private bool _isUpdatingGraph;
    private bool _revealHiddenTasks;

    /// <summary>
    /// Formats one optional duration using the compact runtime clock style shared by the workspace header and graph metrics.
    /// </summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration == null)
        {
            return "--:--";
        }

        TimeSpan resolvedDuration = duration.Value;
        if (resolvedDuration < TimeSpan.Zero)
        {
            resolvedDuration = TimeSpan.Zero;
        }

        return resolvedDuration.TotalHours >= 1
            ? resolvedDuration.ToString(@"h\:mm\:ss")
            : resolvedDuration.ToString(@"mm\:ss");
    }

    /// <summary>
    /// Gets the rendered graph-node collection.
    /// </summary>
    public ObservableCollection<ExecutionNodeViewModel> Nodes => _nodes;

    /// <summary>
    /// Gets the rendered graph-edge collection.
    /// </summary>
    public ObservableCollection<ExecutionEdgeViewModel> Edges => _edges;

    /// <summary>
    /// Gets the current structural layering snapshot used by the canvas to interleave groups and whole edges.
    /// </summary>
    public ExecutionGraphStructureLayeringSnapshot StructureLayering => _layoutResult.StructureLayering;

    /// <summary>
    /// Gets whether the graph is currently rebuilding its internal collections and bounds as one bulk update.
    /// </summary>
    public bool IsUpdatingGraph
    {
        get => _isUpdatingGraph;
        private set => SetProperty(ref _isUpdatingGraph, value);
    }

    /// <summary>
    /// Gets whether the graph currently reveals tasks that are normally collapsed out of the visible hierarchy.
    /// </summary>
    public bool RevealHiddenTasks
    {
        get => _revealHiddenTasks;
        private set => SetProperty(ref _revealHiddenTasks, value);
    }

    /// <summary>
    /// Gets the currently selected graph node for details and task-log binding.
    /// </summary>
    public ExecutionNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (_selectedNode == value)
            {
                return;
            }

            if (_selectedNode != null)
            {
                _selectedNode.IsSelected = false;
            }

            _selectedNode = value;
            if (_selectedNode != null)
            {
                _selectedNode.IsSelected = true;
            }

            RaisePropertyChanged(nameof(SelectedNode));
            RaisePropertyChanged(nameof(SelectedTaskId));
            RaisePropertyChanged(nameof(HasSelectedNode));
        }
    }

    /// <summary>
    /// Gets whether the graph currently has a selected graph node.
    /// </summary>
    public bool HasSelectedNode => SelectedNode != null;

    /// <summary>
    /// Gets the selected task id for details and status binding.
    /// </summary>
    public RuntimeExecutionTaskId? SelectedTaskId => SelectedNode?.Id;

    /// <summary>
    /// Returns the selected task id plus all descendant task ids so the UI can show one hierarchical task subtree without
    /// treating parent tasks specially at runtime.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GetSelectedLogTaskIds()
    {
        return SelectedNode == null
            ? Array.Empty<RuntimeExecutionTaskId>()
            : _projection.GetTaskSubtreeIds(SelectedNode.Id);
    }

    /// <summary>
    /// Gets the total canvas width required to render the current graph.
    /// </summary>
    public double CanvasWidth
    {
        get => _canvasWidth;
        private set => SetProperty(ref _canvasWidth, value);
    }

    /// <summary>
    /// Gets the total canvas height required to render the current graph.
    /// </summary>
    public double CanvasHeight
    {
        get => _canvasHeight;
        private set => SetProperty(ref _canvasHeight, value);
    }

    /// <summary>
    /// Replaces the rendered graph with the provided task set and recomputes the current visible projection.
    /// </summary>
    public void SetGraph(IReadOnlyList<RuntimeExecutionTask>? tasks)
    {
        /* Trace plan-graph rebuild phases separately so option-edit latency can be attributed to projection, layout,
           node reconciliation, or selection restoration rather than treating graph refresh as one opaque cost. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraph.SetGraph")
            .SetTag("plan.has_result", tasks != null)
            .SetTag("plan.task.count", tasks?.Count ?? 0);

        _sourceTasks = tasks?.ToList();
        Dictionary<RuntimeExecutionTaskId, double> retainedMeasuredNodeWidths = tasks == null
            ? new Dictionary<RuntimeExecutionTaskId, double>()
            : _layoutState.ExportRetainedVisibleWidths(tasks, RevealHiddenTasks);
        IsUpdatingGraph = true;
        try
        {
            _layoutState.ResetMeasuredNodeWidths(retainedMeasuredNodeWidths);
            _projection = tasks == null
                ? ExecutionGraphProjection.Empty
                : ExecutionGraphProjection.Create(tasks, RevealHiddenTasks);
            _layoutResult = tasks == null
                ? ExecutionGraphLayoutResult.Empty
                : ExecutionGraphLayoutEngine.Calculate(_projection, _layoutState);
            RebuildVisibleGraph();
            RestoreSelection();
        }
        finally
        {
            IsUpdatingGraph = false;
        }
    }

    /// <summary>
    /// Updates whether the graph should reveal tasks that are normally collapsed and rebuilds the current projection when
    /// that preference changes.
    /// </summary>
    public void SetRevealHiddenTasks(bool revealHiddenTasks)
    {
        if (RevealHiddenTasks == revealHiddenTasks)
        {
            return;
        }

        RevealHiddenTasks = revealHiddenTasks;
        SetGraph(_sourceTasks);
    }

    /// <summary>
    /// Selects one graph node in the rendered plan.
    /// </summary>
    public void SelectNode(ExecutionNodeViewModel? node)
    {
        SelectedNode = node;
    }

    /// <summary>
    /// Attaches the shared Avalonia task view-model registry that graph nodes should wrap instead of constructing local copies.
    /// </summary>
    public void AttachTasks(IReadOnlyDictionary<RuntimeExecutionTaskId, ExecutionTaskViewModel> tasksById)
    {
        _tasksById = tasksById ?? throw new ArgumentNullException(nameof(tasksById));
    }

    /// <summary>
    /// Seeds measured widths from another graph for matching visible task ids so a newly opened runtime graph can reuse
    /// the already-measured preview widths instead of forcing a full first-pass width measurement for the same nodes.
    /// </summary>
    public void ImportMeasuredNodeWidthsFrom(ExecutionGraphViewModel? sourceGraph, IReadOnlyList<RuntimeExecutionTask>? tasks)
    {
        _layoutState.ImportRetainedVisibleWidths(sourceGraph?._layoutState, tasks, RevealHiddenTasks);
    }

    /// <summary>
    /// Recomputes graph layout using the current measured node widths after the canvas has updated them from real XAML
    /// control measurement.
    /// </summary>
    public void Relayout()
    {
        if (_projection.VisibleTaskIds.Count == 0)
        {
            return;
        }

        IsUpdatingGraph = true;
        try
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraph.Relayout")
                .SetTag("visible.node.count", _projection.VisibleTaskIds.Count);
            _layoutResult = ExecutionGraphLayoutEngine.Calculate(_projection, _layoutState);
            ApplyLayoutResult();
            activity.SetTag("edge.count", _edges.Count)
                .SetTag("canvas.width", CanvasWidth)
                .SetTag("canvas.height", CanvasHeight);
        }
        finally
        {
            IsUpdatingGraph = false;
        }
    }

    /// <summary>
    /// Stores the measured natural width for one rendered graph control so layout can use the XAML-owned size instead of
    /// view-model text heuristics.
    /// </summary>
    public bool SetMeasuredNodeWidth(RuntimeExecutionTaskId taskId, double width)
    {
        return _layoutState.TrySetMeasuredNodeWidth(taskId, width);
    }

    /// <summary>
    /// Raises selected-node notifications when an underlying shared task VM changed in a way that could affect the details UI.
    /// </summary>
    public void NotifyTaskStateChanged(RuntimeExecutionTaskId taskId)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraph.NotifyTaskStateChanged")
            .SetTag("task.id", taskId.Value);
        if (!_nodesById.TryGetValue(taskId, out ExecutionNodeViewModel? node))
        {
            activity.SetTag("node.found", false);
            return;
        }

        activity.SetTag("node.found", true)
            .SetTag("selected.node.id", SelectedNode?.Id.Value ?? string.Empty)
            .SetTag("selected.node.is_container", SelectedNode?.IsContainer ?? false)
            .SetTag("node.is_selected", ReferenceEquals(SelectedNode, node));

        if (ReferenceEquals(SelectedNode, node) || (SelectedNode != null && SelectedNode.IsContainer))
        {
            activity.SetTag("selected_node.invalidated", true);
            RaisePropertyChanged(nameof(SelectedNode));
            return;
        }

        activity.SetTag("selected_node.invalidated", false);
    }

    /// <summary>
    /// <summary>
    /// Rebuilds the visible node and edge view-model collections from the latest projection and layout snapshots.
    /// </summary>
    private void RebuildVisibleGraph()
    {
        _nodes.Clear();
        _edges.Clear();
        _nodesById.Clear();

        foreach (RuntimeExecutionTaskId taskId in _projection.VisibleTaskIds)
        {
            if (!_tasksById.TryGetValue(taskId, out ExecutionTaskViewModel? taskViewModel))
            {
                throw new InvalidOperationException($"No shared ExecutionTaskViewModel exists for task '{taskId}'.");
            }

            ExecutionNodeViewModel node = new(taskViewModel);
            if (_layoutResult.NodeLayouts.TryGetValue(taskId, out ExecutionNodeLayout? layout))
            {
                node.ApplyLayout(layout);
            }

            _nodes.Add(node);
            _nodesById[taskId] = node;
        }

        foreach (ExecutionGraphEdgeLayout edgeLayout in _layoutResult.EdgeLayouts)
        {
            ExecutionEdgeViewModel edge = CreateEdgeViewModel(edgeLayout);
            _edges.Add(edge);
        }

        UpdateCanvasSize();
    }

    /// <summary>
    /// Applies the latest layout snapshot to the retained node and edge view models.
    /// </summary>
    private void ApplyLayoutResult()
    {
        foreach ((RuntimeExecutionTaskId taskId, ExecutionNodeViewModel node) in _nodesById)
        {
            if (_layoutResult.NodeLayouts.TryGetValue(taskId, out ExecutionNodeLayout? layout))
            {
                node.ApplyLayout(layout);
            }
        }

        _edges.Clear();
        foreach (ExecutionGraphEdgeLayout edgeLayout in _layoutResult.EdgeLayouts)
        {
            ExecutionEdgeViewModel edge = CreateEdgeViewModel(edgeLayout);
            _edges.Add(edge);
        }

        UpdateCanvasSize();
    }

    /// <summary>
    /// Creates one edge view model from the latest edge-layout snapshot.
    /// </summary>
    private ExecutionEdgeViewModel CreateEdgeViewModel(ExecutionGraphEdgeLayout edgeLayout)
    {
        if (!_nodesById.TryGetValue(edgeLayout.SourceId, out ExecutionNodeViewModel? sourceNode))
        {
            throw new InvalidOperationException($"No visible source node exists for edge '{edgeLayout.SourceId}' → '{edgeLayout.TargetId}'.");
        }

        if (!_nodesById.TryGetValue(edgeLayout.TargetId, out ExecutionNodeViewModel? targetNode))
        {
            throw new InvalidOperationException($"No visible target node exists for edge '{edgeLayout.SourceId}' → '{edgeLayout.TargetId}'.");
        }

        return new ExecutionEdgeViewModel(sourceNode, targetNode, edgeLayout);
    }

    /// <summary>
    /// Updates the canvas size from the laid out node bounds.
    /// </summary>
    private void UpdateCanvasSize()
    {
        CanvasWidth = _layoutResult.CanvasWidth;
        CanvasHeight = _layoutResult.CanvasHeight;
    }

    /// <summary>
    /// Restores the default graph selection after the plan changes by selecting the one real root task when no previous
    /// selection can be preserved.
    /// </summary>
    private void RestoreSelection()
    {
        if (_nodes.Count == 0)
        {
            SelectNode(null);
            return;
        }

        RuntimeExecutionTaskId? previouslySelectedTaskId = SelectedNode?.Id;
        RuntimeExecutionTaskId? restoredSelectionId = previouslySelectedTaskId == null
            ? null
            : _projection.ResolveVisibleSelectionId(previouslySelectedTaskId.Value);
        if (restoredSelectionId != null && _nodesById.TryGetValue(restoredSelectionId.Value, out ExecutionNodeViewModel? existingSelection))
        {
            SelectNode(existingSelection);
            return;
        }

        RuntimeExecutionTaskId? fallbackRootId = _projection.RootTaskIds.Count > 0 ? _projection.RootTaskIds[0] : null;
        SelectNode(fallbackRootId != null && _nodesById.TryGetValue(fallbackRootId.Value, out ExecutionNodeViewModel? rootSelection)
            ? rootSelection
            : _nodes.FirstOrDefault());
    }
}
