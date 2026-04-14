using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Avalonia.ViewModels;
using LocalAutomation.Core;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;
using Shape = Avalonia.Controls.Shapes.Shape;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Hosts the first-pass execution graph canvas, renders nested comment-style group containers, and owns interactive
/// viewport pan/zoom behavior for the graph surface.
/// </summary>
public partial class ExecutionGraphCanvas : UserControl
{
    /* These viewport constants keep wheel zoom predictable while allowing large execution plans to zoom farther out. */
    private const double DefaultZoom = 1.0;
    private const double MinZoom = 0.2;
    private const double MaxZoom = 2.5;
    private const double ZoomStep = 1.15;
    private const double ViewportRecoveryMargin = 40;
    private const double ViewportFitMargin = 12;
    private const double EdgeHitThickness = 10;
    private const int ReconciliationInstrumentationInterval = 10;

    private Canvas? _structureCanvas;
    private Canvas? _taskCanvas;
    private Border? _viewportHost;
    private Border? _graphContentRoot;
    private ExecutionGraphViewModel? _observedGraph;
    private readonly Dictionary<RuntimeExecutionTaskId, ExecutionGroupContainer> _renderedGroupControls = new();
    private readonly Dictionary<RuntimeExecutionTaskId, ExecutionTaskCard> _renderedTaskControls = new();
    private readonly Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), RetainedEdgeVisual> _renderedEdges = new();
    private readonly Dictionary<RuntimeExecutionTaskId, double> _pendingNodeWidthUpdates = new();
    private bool _isPanning;
    private bool _refreshQueuedWhileUpdating;
    private bool _pendingViewportAdjustment;
    private bool _suppressGraphNotifications;
    private Point _lastPanPoint;
    private double _zoom = DefaultZoom;
    private double _panX;
    private double _panY;
    private bool _hasViewportState;
    private int _reconciliationCount;
    private NodeWidthUpdatePhase _nodeWidthUpdatePhase;

    /// <summary>
    /// Represents the lifecycle of one coalesced batch of intrinsic-width updates flowing from visible node controls back
    /// into graph relayout.
    /// </summary>
    private enum NodeWidthUpdatePhase
    {
        Idle,
        Scheduled,
        Applying
    }

    /// <summary>
    /// Initializes the execution graph canvas.
    /// </summary>
    public ExecutionGraphCanvas()
    {
        InitializeComponent();
        DataContextChanged += HandleDataContextChanged;
        AttachedToVisualTree += HandleAttachedToVisualTree;
    }

    /// <summary>
    /// Selects the clicked graph node inside the current graph view model.
    /// </summary>
    private void ExecutionNode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExecutionGraphViewModel graph || sender is not Control { DataContext: ExecutionNodeViewModel node })
        {
            return;
        }

        graph.SelectNode(node);
        e.Handled = true;
    }

    /// <summary>
    /// Starts a background-drag pan gesture when the user presses on empty viewport space.
    /// </summary>
    private void ViewportHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewportHost == null)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(_viewportHost);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Control sourceControl && IsNodeInteractionSource(sourceControl))
        {
            return;
        }

        _isPanning = true;
        _lastPanPoint = point.Position;
        e.Pointer.Capture(_viewportHost);
        e.Handled = true;
    }

    /// <summary>
    /// Updates the viewport offset while the user drags the graph background.
    /// </summary>
    private void ViewportHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || _viewportHost == null)
        {
            return;
        }

        Point currentPoint = e.GetPosition(_viewportHost);
        Vector delta = currentPoint - _lastPanPoint;
        _lastPanPoint = currentPoint;
        _panX += delta.X;
        _panY += delta.Y;
        _hasViewportState = true;
        ApplyViewportTransform();
        e.Handled = true;
    }

    /// <summary>
    /// Finishes the current background-pan gesture.
    /// </summary>
    private void ViewportHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndPanGesture(e.Pointer);
    }

    /// <summary>
    /// Clears the pan gesture if pointer capture is interrupted.
    /// </summary>
    private void ViewportHost_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndPanGesture(e.Pointer);
    }

    /// <summary>
    /// Zooms the viewport around the pointer position so the graph feels anchored under the cursor.
    /// </summary>
    private void ViewportHost_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_viewportHost == null || _graphContentRoot == null)
        {
            return;
        }

        Point pointerPosition = e.GetPosition(_viewportHost);
        Point worldPoint = ViewportToWorld(pointerPosition);
        double zoomFactor = e.Delta.Y >= 0 ? ZoomStep : 1.0 / ZoomStep;
        double newZoom = Math.Clamp(_zoom * zoomFactor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < 0.0001)
        {
            return;
        }

        _zoom = newZoom;
        _panX = pointerPosition.X - (worldPoint.X * _zoom);
        _panY = pointerPosition.Y - (worldPoint.Y * _zoom);
        _hasViewportState = true;
        ApplyViewportTransform();
        e.Handled = true;
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the graph canvas.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _viewportHost = this.FindControl<Border>("ViewportHost");
        _graphContentRoot = this.FindControl<Border>("GraphContentRoot");
        _structureCanvas = this.FindControl<Canvas>("StructureCanvas");
        _taskCanvas = this.FindControl<Canvas>("TaskCanvas");
        if (_viewportHost != null)
        {
            _viewportHost.SizeChanged += HandleViewportHostSizeChanged;
        }
    }

    /// <summary>
    /// Rebinds graph collection listeners whenever the control receives a different graph view model.
    /// </summary>
    private void HandleDataContextChanged(object? sender, EventArgs e)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.HandleDataContextChanged")
            .SetTag("previous.graph.hash", _observedGraph?.GetHashCode() ?? 0)
            .SetTag("next.graph.hash", (DataContext as ExecutionGraphViewModel)?.GetHashCode() ?? 0)
            .SetTag("pending.render", _refreshQueuedWhileUpdating)
            .SetTag("pending.width.update.count", _pendingNodeWidthUpdates.Count);

        if (_observedGraph != null)
        {
            _observedGraph.Nodes.CollectionChanged -= HandleGraphCollectionChanged;
            _observedGraph.Edges.CollectionChanged -= HandleGraphCollectionChanged;
            _observedGraph.PropertyChanged -= HandleGraphPropertyChanged;
        }

        _observedGraph = DataContext as ExecutionGraphViewModel;
        _pendingNodeWidthUpdates.Clear();
        _nodeWidthUpdatePhase = NodeWidthUpdatePhase.Idle;
        activity.SetTag("graph.present", _observedGraph != null);
        if (_observedGraph != null)
        {
            _observedGraph.Nodes.CollectionChanged += HandleGraphCollectionChanged;
            _observedGraph.Edges.CollectionChanged += HandleGraphCollectionChanged;
            _observedGraph.PropertyChanged += HandleGraphPropertyChanged;
        }

        /* Re-evaluate the viewport every time the observed graph changes. First render fits the graph, while later tab
           switches preserve the current zoom/pan and only apply minimal recovery when the graph has drifted too far out
           of view. Defer the actual fit/recovery until the next real viewport layout pass so tab switches do not reuse
           stale bounds from the previously visible workspace mode. */
        _pendingViewportAdjustment = true;
        using (PerformanceActivityScope renderActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.HandleDataContextChanged.RenderGraph"))
        {
            RenderGraph(trigger: "DataContextChanged");
        }

        if (_viewportHost != null && _viewportHost.Bounds.Width > 0 && _viewportHost.Bounds.Height > 0)
        {
            using PerformanceActivityScope postViewportAdjustmentActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.HandleDataContextChanged.PostViewportAdjustment");
            Dispatcher.UIThread.Post(ApplyPendingViewportAdjustment, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Applies the deferred first-fit pass once the control is attached and participating in layout.
    /// </summary>
    private void HandleAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        ApplyPendingViewportAdjustment();
    }

    /// <summary>
    /// Retries the initial fit when the viewport receives its first real size after graph content has already rendered.
    /// </summary>
    private void HandleViewportHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyPendingViewportAdjustment();
    }

    /// <summary>
    /// Re-renders the graph when the graph-node or graph-edge collections change.
    /// </summary>
    private void HandleGraphCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        /* Graph layout can internally relayout once after detached control measurement. That path already culminates in a
           final explicit render, so the observer should ignore intermediate collection notifications raised inside the
           same render transaction. */
        if (_suppressGraphNotifications)
        {
            return;
        }

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.CollectionChanged")
            .SetTag("source", ReferenceEquals(sender, _observedGraph?.Nodes) ? "Nodes" : ReferenceEquals(sender, _observedGraph?.Edges) ? "Edges" : string.Empty)
            .SetTag("action", e.Action.ToString())
            .SetTag("new_item.count", e.NewItems?.Count ?? 0)
            .SetTag("old_item.count", e.OldItems?.Count ?? 0);

        string trigger = $"CollectionChanged:{e.Action}";
        if (TryQueueRenderWhileUpdating(trigger))
        {
            activity.SetTag("render.deferred", true);
            return;
        }

        RenderGraph(trigger: trigger);
    }

    /// <summary>
    /// Re-renders the graph when layout-dependent graph properties change.
    /// </summary>
    private void HandleGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        /* Graph relayout mutates the node and edge collections that already drive explicit visible reconciliation, so the
           canvas ignores graph notifications raised from inside its own width-application transaction. */
        if (_suppressGraphNotifications)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ExecutionGraphViewModel.IsUpdatingGraph), StringComparison.Ordinal))
        {
            if (_observedGraph?.IsUpdatingGraph == false && _refreshQueuedWhileUpdating)
            {
                _refreshQueuedWhileUpdating = false;
                RenderGraph(trigger: "Deferred:GraphUpdate");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(ExecutionGraphViewModel.CanvasWidth), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionGraphViewModel.CanvasHeight), StringComparison.Ordinal))
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.PropertyChanged")
                .SetTag("property.name", e.PropertyName ?? string.Empty);

            string trigger = $"PropertyChanged:{e.PropertyName}";
            if (TryQueueRenderWhileUpdating(trigger))
            {
                activity.SetTag("render.deferred", true);
                return;
            }

            RenderGraph(trigger: trigger);
        }
    }

    /// <summary>
    /// Queues one render to run after the current bulk graph update completes so collection and canvas-size churn do not
    /// rebuild the entire visual tree multiple times during one SetPlan pass.
    /// </summary>
    private bool TryQueueRenderWhileUpdating(string trigger)
    {
        if (_observedGraph?.IsUpdatingGraph != true)
        {
            return false;
        }

        _refreshQueuedWhileUpdating = true;
        return true;
    }

    /// <summary>
    /// Rebuilds the graph canvas from the current graph view model so comment containers, edges, and task cards render
    /// in a deterministic back-to-front order.
    /// </summary>
    private void RenderGraph(string trigger)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.RenderGraph")
            .SetTag("trigger", trigger);

        if (!TryGetGraphSurfaces(out GraphSurfaces surfaces))
        {
            activity.SetTag("render.skipped", _observedGraph == null ? "MissingGraph" : "MissingCanvas");
            return;
        }

        int groupCount = surfaces.Graph.Nodes.Count(node => node.IsContainer);
        int leafCount = surfaces.Graph.Nodes.Count - groupCount;
        activity.SetTag("group.count", groupCount)
            .SetTag("leaf.count", leafCount)
            .SetTag("edge.count", surfaces.Graph.Edges.Count);

        using (PerformanceActivityScope reconcileActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ReconcileVisibleGraphLayers"))
        {
            ReconcileVisibleGraphLayers(surfaces.Graph, activity, reconcileActivity);
        }

        using (PerformanceActivityScope transformActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ApplyViewportTransform"))
        {
            ApplyViewportTransform();
            transformActivity.SetTag("zoom", _zoom.ToString("0.###"))
                .SetTag("pan.x", _panX.ToString("0.###"))
                .SetTag("pan.y", _panY.ToString("0.###"));
        }

        activity.SetTag("render.child.count", surfaces.StructureCanvas.Children.Count + surfaces.TaskCanvas.Children.Count);

        using (PerformanceActivityScope viewportAdjustmentActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ApplyPendingViewportAdjustment"))
        {
            ApplyPendingViewportAdjustment();
            viewportAdjustmentActivity.SetTag("pending.adjustment", _pendingViewportAdjustment)
                .SetTag("has.viewport_state", _hasViewportState);
        }
    }

    /// <summary>
    /// Queues one intrinsic-width update coming from a visible node control so the graph relayout runs once per burst of
    /// width changes instead of once per individual control notification.
    /// </summary>
    private void EnqueuePendingNodeWidthUpdate(RuntimeExecutionTaskId taskId, double intrinsicWidth)
    {
        if (_observedGraph == null)
        {
            return;
        }

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.EnqueuePendingNodeWidthUpdate")
            .SetTag("task.id", taskId.Value)
            .SetTag("reported.width", intrinsicWidth.ToString("0.###"));

        _pendingNodeWidthUpdates[taskId] = intrinsicWidth;
        activity.SetTag("pending.node.width.count", _pendingNodeWidthUpdates.Count)
            .SetTag("flush.already.pending", _nodeWidthUpdatePhase != NodeWidthUpdatePhase.Idle);
        if (_nodeWidthUpdatePhase != NodeWidthUpdatePhase.Idle)
        {
            return;
        }

        _nodeWidthUpdatePhase = NodeWidthUpdatePhase.Scheduled;
        Dispatcher.UIThread.Post(FlushPendingNodeWidthUpdates, DispatcherPriority.Render);
    }

    /// <summary>
    /// Applies one coalesced batch of intrinsic node-width updates from visible controls through the shared graph layout
    /// cache, then rerenders the visible graph from the resulting relayout.
    /// </summary>
    private void FlushPendingNodeWidthUpdates()
    {
        if (_observedGraph == null || _pendingNodeWidthUpdates.Count == 0)
        {
            _nodeWidthUpdatePhase = NodeWidthUpdatePhase.Idle;
            return;
        }

        if (_observedGraph.IsUpdatingGraph)
        {
            _nodeWidthUpdatePhase = NodeWidthUpdatePhase.Scheduled;
            Dispatcher.UIThread.Post(FlushPendingNodeWidthUpdates, DispatcherPriority.Render);
            return;
        }

        _nodeWidthUpdatePhase = NodeWidthUpdatePhase.Applying;

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.FlushPendingNodeWidthUpdates")
            .SetTag("pending.node.width.count", _pendingNodeWidthUpdates.Count);
        KeyValuePair<RuntimeExecutionTaskId, double>[] updates = _pendingNodeWidthUpdates.ToArray();
        _pendingNodeWidthUpdates.Clear();

        int changedWidthCount = 0;
        foreach ((RuntimeExecutionTaskId taskId, double width) in updates)
        {
            if (_observedGraph.SetMeasuredNodeWidth(taskId, width))
            {
                changedWidthCount++;
            }
        }

        activity.SetTag("node.width.changed.count", changedWidthCount);
        try
        {
            if (changedWidthCount == 0)
            {
                return;
            }

            _suppressGraphNotifications = true;
            _observedGraph.Relayout();
            RenderGraph(trigger: "Deferred:IntrinsicWidthChanged");
        }
        finally
        {
            _suppressGraphNotifications = false;
            if (_pendingNodeWidthUpdates.Count > 0)
            {
                _nodeWidthUpdatePhase = NodeWidthUpdatePhase.Scheduled;
                Dispatcher.UIThread.Post(FlushPendingNodeWidthUpdates, DispatcherPriority.Render);
            }
            else
            {
                _nodeWidthUpdatePhase = NodeWidthUpdatePhase.Idle;
            }
        }
    }

    /// <summary>
    /// Reconciles retained group/task controls plus the retained edge layer from the current graph snapshot.
    /// </summary>
    private void ReconcileVisibleGraphLayers(ExecutionGraphViewModel graph, PerformanceActivityScope activity, PerformanceActivityScope parentActivity)
    {
        int reconciliationSequence = System.Threading.Interlocked.Increment(ref _reconciliationCount);
        using PerformanceActivityScope sampledReconciliationActivity = reconciliationSequence % ReconciliationInstrumentationInterval == 0
            ? PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ReconcileLayers")
                .SetTag("sequence", reconciliationSequence)
                .SetTag("group.count", graph.Nodes.Count(node => node.IsContainer))
                .SetTag("leaf.count", graph.Nodes.Count(node => !node.IsContainer))
                .SetTag("edge.count", graph.Edges.Count)
            : default;

        (int retainedCount, int createdCount, int removedCount, int recreatedForRoleChangeCount) groupStats;
        using (PerformanceActivityScope groupActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ReconcileGroupControls"))
        {
            groupStats = ReconcileGroupControls(graph);
            SetReconciliationTags(groupActivity, prefix: "group", groupStats);
        }

        (int retainedCount, int createdCount, int removedCount, int recreatedForRoleChangeCount) taskStats;
        using (PerformanceActivityScope taskActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ReconcileTaskControls"))
        {
            taskStats = ReconcileTaskControls(graph);
            SetReconciliationTags(taskActivity, prefix: "task", taskStats);
        }

        EdgeLayerStats edgeStats;
        using (PerformanceActivityScope edgeActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ReconcileEdgeLayer"))
        {
            edgeStats = ReconcileEdgeLayer(graph);
            SetEdgeLayerTags(edgeActivity, edgeStats);
        }

        SetReconciliationSummaryTags(parentActivity, groupStats, taskStats, edgeStats);
        SetReconciliationSummaryTags(activity, groupStats, taskStats, edgeStats);
        SetReconciliationSummaryTags(sampledReconciliationActivity, groupStats, taskStats, edgeStats);
    }

    /// <summary>
    /// Reconciles retained group-container controls for nodes that currently act as containers.
    /// </summary>
    private (int retainedCount, int createdCount, int removedCount, int recreatedForRoleChangeCount) ReconcileGroupControls(ExecutionGraphViewModel graph)
    {
        if (_structureCanvas == null)
        {
            return default;
        }

        List<ExecutionNodeViewModel> groups = graph.Nodes.Where(node => node.IsContainer).OrderByDescending(node => node.Width * node.Height).ToList();
        HashSet<RuntimeExecutionTaskId> groupIds = groups.Select(group => group.Id).ToHashSet();
        int removedCount = RemoveStaleGroups(groupIds);
        int recreatedForRoleChangeCount = RemoveStaleTasksFor(groups);
        (int retainedCount, int createdCount) = ReconcileGroups(groups);
        return (retainedCount, createdCount, removedCount, recreatedForRoleChangeCount);
    }

    /// <summary>
    /// Reconciles retained task-card controls for nodes that currently render as leaves.
    /// </summary>
    private (int retainedCount, int createdCount, int removedCount, int recreatedForRoleChangeCount) ReconcileTaskControls(ExecutionGraphViewModel graph)
    {
        if (_taskCanvas == null)
        {
            return default;
        }

        List<ExecutionNodeViewModel> leaves = graph.Nodes.Where(node => !node.IsContainer).ToList();
        HashSet<RuntimeExecutionTaskId> leafIds = leaves.Select(node => node.Id).ToHashSet();
        int removedCount = RemoveStaleTasks(leafIds);
        int recreatedForRoleChangeCount = RemoveStaleGroupsFor(leaves);
        (int retainedCount, int createdCount) = ReconcileTasks(leaves);

        /* Leaf task cards do not visually stack on top of each other the way nested group containers do, so preserving
           exact child order in the task layer only creates expensive detach/re-add churn without changing what the user
           sees. Keep cards mounted in their existing order and only add/remove cards when the visible leaf set changes. */
        return (retainedCount, createdCount, removedCount, recreatedForRoleChangeCount);
    }

    /// <summary>
    /// Removes retained group controls that no longer exist in the current visible group set.
    /// </summary>
    private int RemoveStaleGroups(HashSet<RuntimeExecutionTaskId> activeGroupIds)
    {
        int removedCount = 0;
        foreach ((RuntimeExecutionTaskId taskId, ExecutionGroupContainer groupControl) in _renderedGroupControls.ToList())
        {
            if (activeGroupIds.Contains(taskId))
            {
                continue;
            }

            DetachIntrinsicWidthObserver(groupControl);
            _structureCanvas!.Children.Remove(groupControl);
            _renderedGroupControls.Remove(taskId);
            removedCount++;
        }

        return removedCount;
    }

    /// <summary>
    /// Removes retained task-card controls that no longer exist in the current visible leaf set.
    /// </summary>
    private int RemoveStaleTasks(HashSet<RuntimeExecutionTaskId> activeLeafIds)
    {
        int removedCount = 0;
        foreach ((RuntimeExecutionTaskId taskId, ExecutionTaskCard taskControl) in _renderedTaskControls.ToList())
        {
            if (activeLeafIds.Contains(taskId))
            {
                continue;
            }

            DetachIntrinsicWidthObserver(taskControl);
            _taskCanvas!.Children.Remove(taskControl);
            _renderedTaskControls.Remove(taskId);
            removedCount++;
        }

        return removedCount;
    }

    /// <summary>
    /// Removes task-card controls for nodes that now render as groups so the group path can recreate the right control kind.
    /// </summary>
    private int RemoveStaleTasksFor(IEnumerable<ExecutionNodeViewModel> groups)
    {
        int recreatedForRoleChangeCount = 0;
        foreach (ExecutionNodeViewModel group in groups)
        {
            if (!_renderedTaskControls.Remove(group.Id, out ExecutionTaskCard? staleTaskControl))
            {
                continue;
            }

            DetachIntrinsicWidthObserver(staleTaskControl);
            _taskCanvas?.Children.Remove(staleTaskControl);
            recreatedForRoleChangeCount++;
        }

        return recreatedForRoleChangeCount;
    }

    /// <summary>
    /// Removes group controls for nodes that now render as leaves so the leaf path can recreate the right control kind.
    /// </summary>
    private int RemoveStaleGroupsFor(IEnumerable<ExecutionNodeViewModel> leaves)
    {
        int recreatedForRoleChangeCount = 0;
        foreach (ExecutionNodeViewModel leaf in leaves)
        {
            if (!_renderedGroupControls.Remove(leaf.Id, out ExecutionGroupContainer? staleGroupControl))
            {
                continue;
            }

            DetachIntrinsicWidthObserver(staleGroupControl);
            _structureCanvas?.Children.Remove(staleGroupControl);
            recreatedForRoleChangeCount++;
        }

        return recreatedForRoleChangeCount;
    }

    /// <summary>
    /// Retains or creates the visible group controls for the current graph snapshot.
    /// </summary>
    private (int retainedCount, int createdCount) ReconcileGroups(IEnumerable<ExecutionNodeViewModel> groups)
    {
        int retainedCount = 0;
        int createdCount = 0;
        foreach (ExecutionNodeViewModel group in groups)
        {
            if (_renderedGroupControls.TryGetValue(group.Id, out ExecutionGroupContainer? existingGroupControl))
            {
                UpdateGroupControl(existingGroupControl, group);
                retainedCount++;
                continue;
            }

            ExecutionGroupContainer groupControl = CreateGroupControl(group);
            _renderedGroupControls[group.Id] = groupControl;
            _structureCanvas!.Children.Add(groupControl);
            createdCount++;
        }

        return (retainedCount, createdCount);
    }

    /// <summary>
    /// Retains or creates the visible task-card controls for the current graph snapshot.
    /// </summary>
    private (int retainedCount, int createdCount) ReconcileTasks(IEnumerable<ExecutionNodeViewModel> leaves)
    {
        int retainedCount = 0;
        int createdCount = 0;
        foreach (ExecutionNodeViewModel leaf in leaves)
        {
            if (_renderedTaskControls.TryGetValue(leaf.Id, out ExecutionTaskCard? existingTaskControl))
            {
                UpdateTaskControl(existingTaskControl, leaf);
                retainedCount++;
                continue;
            }

            ExecutionTaskCard taskControl = CreateTaskControl(leaf);
            _renderedTaskControls[leaf.Id] = taskControl;
            _taskCanvas!.Children.Add(taskControl);
            createdCount++;
        }

        return (retainedCount, createdCount);
    }

    /// <summary>
    /// Applies one common set of retained-control reconciliation tags for either the group layer or the task layer.
    /// </summary>
    private static void SetReconciliationTags(
        PerformanceActivityScope activity,
        string prefix,
        (int retainedCount, int createdCount, int removedCount, int recreatedForRoleChangeCount) stats)
    {
        activity.SetTag($"retained.{prefix}.count", stats.retainedCount)
            .SetTag($"created.{prefix}.count", stats.createdCount)
            .SetTag($"removed.{prefix}.count", stats.removedCount)
            .SetTag($"recreated.{prefix}.kind_change.count", stats.recreatedForRoleChangeCount);
    }

    /// <summary>
    /// Applies the combined reconciliation summary tags shared by the render, parent, and sampled activities.
    /// </summary>
    private static void SetReconciliationSummaryTags(
        PerformanceActivityScope activity,
        (int retainedCount, int createdCount, int removedCount, int recreatedForRoleChangeCount) groupStats,
        (int retainedCount, int createdCount, int removedCount, int recreatedForRoleChangeCount) taskStats,
        EdgeLayerStats edgeStats)
    {
        SetReconciliationTags(activity, prefix: "group", groupStats);
        SetReconciliationTags(activity, prefix: "task", taskStats);
        SetEdgeLayerTags(activity, edgeStats);
    }

    /// <summary>
    /// Applies the combined retained-edge reconciliation tags shared by the edge activity and higher-level render spans.
    /// </summary>
    private static void SetEdgeLayerTags(PerformanceActivityScope activity, EdgeLayerStats stats)
    {
        activity.SetTag("retained.edge.count", stats.RetainedCount)
            .SetTag("created.edge.count", stats.CreatedCount)
            .SetTag("removed.edge.count", stats.RemovedCount)
            .SetTag("logical.edge.count", stats.LogicalCount)
            .SetTag("edge.path.count", stats.PathCount)
            .SetTag("total.route.point.count", stats.TotalRoutePointCount)
            .SetTag("max.route.point.count", stats.MaxRoutePointCount)
            .SetTag("avg.route.point.count", stats.AverageRoutePointCount);
    }

    /// <summary>
    /// Reconciles retained edge controls inside the shared structural canvas while leaving retained node controls and
    /// task cards mounted in place.
    /// </summary>
    private EdgeLayerStats ReconcileEdgeLayer(ExecutionGraphViewModel graph)
    {
        if (_structureCanvas == null)
        {
            return default;
        }

        List<ExecutionEdgeViewModel> edges = graph.Edges.ToList();
        int logicalEdgeCount = edges.Count;
        int totalRoutePointCount = edges.Sum(edge => edge.RoutePointCount);
        int maxRoutePointCount = edges.Count == 0 ? 0 : edges.Max(edge => edge.RoutePointCount);
        HashSet<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId)> activeEdgeKeys = edges
            .Select(edge => edge.EdgeKey)
            .ToHashSet();

        /* Capture only stale-edge teardown cost so steady-state relayouts make it obvious whether the remaining hitch
           comes from retained-edge updates or from structural edge churn. */
        int removedCount;
        using (PerformanceActivityScope removeActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.RemoveRetainedEdges"))
        {
            removeActivity.SetTag("logical.edge.count", _renderedEdges.Count)
                .SetTag("edge.path.count", _renderedEdges.Count * 2);
            removedCount = RemoveStaleEdges(activeEdgeKeys);
            removeActivity.SetTag("removed.edge.count", removedCount);
        }

        List<ExecutionEdgeViewModel> retainedEdges = new();
        List<ExecutionEdgeViewModel> createdEdges = new();
        foreach (ExecutionEdgeViewModel edge in edges)
        {
            if (_renderedEdges.ContainsKey(edge.EdgeKey))
            {
                retainedEdges.Add(edge);
                continue;
            }

            createdEdges.Add(edge);
        }

        /* Update retained edge visuals in place so steady-state width relayout spends time only on geometry refresh and
           z-order assignment instead of on tearing down and rebuilding every path control. */
        using (PerformanceActivityScope updateActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.UpdateRetainedEdges"))
        {
            updateActivity.SetTag("logical.edge.count", retainedEdges.Count)
                .SetTag("edge.path.count", retainedEdges.Count * 2);
            foreach (ExecutionEdgeViewModel edge in retainedEdges)
            {
                UpdateEdgeVisual(_renderedEdges[edge.EdgeKey], edge);
            }
        }

        /* Record how much time the canvas spends allocating genuinely new edge controls so traces can separate first-time
           visual creation from the cheaper retained-edge update path. */
        using (PerformanceActivityScope createActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.CreateEdgeControls"))
        {
            createActivity.SetTag("logical.edge.count", createdEdges.Count)
                .SetTag("edge.path.count", createdEdges.Count * 2);
            foreach (ExecutionEdgeViewModel edge in createdEdges)
            {
                RetainedEdgeVisual edgeVisual = CreateEdgeVisual(edge);
                _renderedEdges[edge.EdgeKey] = edgeVisual;
                _structureCanvas.Children.Add(edgeVisual.VisiblePath);
                _structureCanvas.Children.Add(edgeVisual.HoverTargetPath);
            }
        }

        /* Whole-edge layering is intentionally separate from control creation so traces can show whether the current
           hitch comes from retained-edge updates or from constraint-based z-index assignment across groups and edges. */
        using (PerformanceActivityScope zOrderActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ApplyStructureZOrder"))
        {
            zOrderActivity.SetTag("logical.edge.count", logicalEdgeCount)
                .SetTag("group.count", graph.StructureLayering.OrderedGroupIds.Count);
            ApplyStructureZOrder(graph);
        }

        return new EdgeLayerStats(
            retainedEdges.Count,
            createdEdges.Count,
            removedCount,
            _renderedEdges.Count * 2,
            logicalEdgeCount,
            totalRoutePointCount,
            maxRoutePointCount);
    }

    /// <summary>
    /// Removes retained edge visuals that no longer exist in the current visible edge set.
    /// </summary>
    private int RemoveStaleEdges(HashSet<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId)> activeEdgeKeys)
    {
        int removedCount = 0;
        foreach (((RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId) edgeKey, RetainedEdgeVisual edgeVisual) in _renderedEdges.ToList())
        {
            if (activeEdgeKeys.Contains(edgeKey))
            {
                continue;
            }

            edgeVisual.Dispose();
            _structureCanvas!.Children.Remove(edgeVisual.VisiblePath);
            _structureCanvas.Children.Remove(edgeVisual.HoverTargetPath);
            _renderedEdges.Remove(edgeKey);
            removedCount++;
        }

        return removedCount;
    }

    /// <summary>
    /// Creates the visible edge path plus a transparent hover target that toggles the same visual path classes.
    /// </summary>
    private static RetainedEdgeVisual CreateEdgeVisual(ExecutionEdgeViewModel edge)
    {
        ShapePath visiblePath = new();

        /* Semantic classes drive tinting from XAML, so the edge uses the same status language as the rest of the
           execution graph without hard-coded color values in view models. */
        visiblePath.Classes.Add("execution-edge");

        ShapePath hoverTargetPath = new()
        {
            Stroke = Brushes.Transparent,
            StrokeThickness = EdgeHitThickness,
            IsHitTestVisible = true
        };

        /* The hover target shares the same geometry as the visible path but never paints anything, which makes the edge
           easy to interact with while preserving the slimmer rendered line. */
        RetainedEdgeVisual edgeVisual = new(visiblePath, hoverTargetPath);
        edgeVisual.Update(edge);
        return edgeVisual;
    }

    /// <summary>
    /// Updates one retained edge visual from the latest edge view model and shared geometry.
    /// </summary>
    private static void UpdateEdgeVisual(RetainedEdgeVisual edgeVisual, ExecutionEdgeViewModel edge)
    {
        /* Measure the full retained-edge update boundary so traces can compare geometry creation with the later path
           property updates and task-status synchronization that happen on the same visual wrapper. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.UpdateEdgeVisual")
            .SetTag("source.id", edge.Source.Id.Value)
            .SetTag("target.id", edge.Target.Id.Value)
            .SetTag("route.point.count", edge.RoutePointCount);
        edgeVisual.Update(edge);
    }

    /// <summary>
    /// Applies one shared back-to-front ordering across groups and whole edges so disjoint unrelated groups can sit above
    /// or below a given dependency path without forcing segment-level clipping.
    /// </summary>
    private void ApplyStructureZOrder(ExecutionGraphViewModel graph)
    {
        if (_structureCanvas == null)
        {
            return;
        }

        ExecutionGraphStructureLayeringSnapshot layering = graph.StructureLayering;
        Dictionary<RuntimeExecutionTaskId, int> groupBaseZ = layering.OrderedGroupIds
            .Select((groupId, index) => (groupId, index))
            .ToDictionary(entry => entry.groupId, entry => entry.index * 2);

        /* Group containers keep their deterministic relative order, while each edge is placed into the highest valid slot
           that still stays below unrelated crossed groups and above the groups that own one of its endpoints. */
        foreach (RuntimeExecutionTaskId groupId in layering.OrderedGroupIds)
        {
            if (_renderedGroupControls.TryGetValue(groupId, out ExecutionGroupContainer? control))
            {
                control.ZIndex = groupBaseZ[groupId];
            }
        }

        foreach (ExecutionEdgeViewModel edge in graph.Edges)
        {
            if (!layering.EdgeConstraints.TryGetValue((edge.Source.Id, edge.Target.Id), out ExecutionGraphEdgeLayeringConstraints? constraints))
            {
                continue;
            }

            int minimumEdgeZ = -1;
            foreach (RuntimeExecutionTaskId groupId in constraints.GroupsToRenderAbove)
            {
                if (groupBaseZ.TryGetValue(groupId, out int groupZ))
                {
                    minimumEdgeZ = Math.Max(minimumEdgeZ, groupZ + 1);
                }
            }

            int maximumEdgeZ = (layering.OrderedGroupIds.Count * 2) + 1;
            foreach (RuntimeExecutionTaskId groupId in constraints.GroupsToRenderBelow)
            {
                if (groupBaseZ.TryGetValue(groupId, out int groupZ))
                {
                    maximumEdgeZ = Math.Min(maximumEdgeZ, groupZ - 1);
                }
            }

            int resolvedEdgeZ = minimumEdgeZ <= maximumEdgeZ ? maximumEdgeZ : minimumEdgeZ;
            if (_renderedEdges.TryGetValue(edge.EdgeKey, out RetainedEdgeVisual? edgeVisual) && edgeVisual != null)
            {
                edgeVisual.SetZIndex(resolvedEdgeZ);
            }
        }
    }

    /// <summary>
    /// Creates one XAML-backed group control and positions it on the graph canvas.
    /// </summary>
    private ExecutionGroupContainer CreateGroupControl(ExecutionNodeViewModel group)
    {
        ExecutionGroupContainer container = new()
        {
            GroupWidth = group.Width,
            GroupHeight = group.Height,
            HeaderHeight = ExecutionGraphLayoutSettings.GroupHeaderHeight,
            DataContext = group,
        };

        container.Invoked += ExecutionNode_Click;
        AttachIntrinsicWidthObserver(container);

        Canvas.SetLeft(container, group.X);
        Canvas.SetTop(container, group.Y);
        return container;
    }

    /// <summary>
    /// Updates a retained group control from the latest node bounds and view model instance.
    /// </summary>
    private static void UpdateGroupControl(ExecutionGroupContainer container, ExecutionNodeViewModel group)
    {
        container.DataContext = group;
        container.GroupWidth = group.Width;
        container.GroupHeight = group.Height;
        container.HeaderHeight = ExecutionGraphLayoutSettings.GroupHeaderHeight;
        Canvas.SetLeft(container, group.X);
        Canvas.SetTop(container, group.Y);
    }

    /// <summary>
    /// Creates one XAML-backed task-card control and positions it on the graph canvas.
    /// </summary>
    private ExecutionTaskCard CreateTaskControl(ExecutionNodeViewModel node)
    {
        ExecutionTaskCard card = new()
        {
            CardHeight = node.Height,
            DataContext = node
        };

        card.Invoked += ExecutionNode_Click;
        AttachIntrinsicWidthObserver(card);

        Canvas.SetLeft(card, node.X);
        Canvas.SetTop(card, node.Y);
        return card;
    }

    /// <summary>
    /// Updates a retained task-card control from the latest node bounds and view model instance.
    /// </summary>
    private static void UpdateTaskControl(ExecutionTaskCard card, ExecutionNodeViewModel node)
    {
        card.DataContext = node;
        card.CardHeight = node.Height;
        Canvas.SetLeft(card, node.X);
        Canvas.SetTop(card, node.Y);
    }

    /// <summary>
    /// Attaches one retained node control to its intrinsic-width property so the canvas can batch relayout from real
    /// visible-content width changes.
    /// </summary>
    private void AttachIntrinsicWidthObserver(ExecutionGraphNodeControlBase control)
    {
        ((INotifyPropertyChanged)control).PropertyChanged += HandleIntrinsicWidthChanged;
    }

    /// <summary>
    /// Removes the intrinsic-width observer for a retained node control leaving the visible layer.
    /// </summary>
    private void DetachIntrinsicWidthObserver(ExecutionGraphNodeControlBase control)
    {
        ((INotifyPropertyChanged)control).PropertyChanged -= HandleIntrinsicWidthChanged;
    }

    /// <summary>
    /// Observes intrinsic width changes from one retained node control and forwards them into the coalesced graph width
    /// update queue.
    /// </summary>
    private void HandleIntrinsicWidthChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ExecutionGraphNodeControlBase.IntrinsicNodeWidth), StringComparison.Ordinal) ||
            sender is not ExecutionGraphNodeControlBase control ||
            control.DataContext is not ExecutionNodeViewModel node)
        {
            return;
        }

        EnqueuePendingNodeWidthUpdate(node.Id, control.IntrinsicNodeWidth);
    }

    /// <summary>
    /// Carries one summarized retained-edge reconciliation result so the edge activity and higher-level render spans log
    /// the same structural counts and route complexity metrics.
    /// </summary>
    private readonly record struct EdgeLayerStats(
        int RetainedCount,
        int CreatedCount,
        int RemovedCount,
        int PathCount,
        int LogicalCount,
        int TotalRoutePointCount,
        int MaxRoutePointCount)
    {
        /// <summary>
        /// Gets the average routed point count per logical edge using the same compact format as the telemetry output.
        /// </summary>
        public string AverageRoutePointCount => LogicalCount == 0 ? "0" : ((double)TotalRoutePointCount / LogicalCount).ToString("0.##");
    }

    /// <summary>
    /// Owns the visible and hover path controls plus the target-task status subscription for one retained logical edge.
    /// </summary>
    private sealed class RetainedEdgeVisual : IDisposable
    {
        private ExecutionTaskViewModel? _observedTask;
        private PropertyChangedEventHandler? _taskStatusChangedHandler;

        /// <summary>
        /// Creates one retained edge visual wrapper around the two path controls used for display and hit testing.
        /// </summary>
        public RetainedEdgeVisual(ShapePath visiblePath, ShapePath hoverTargetPath)
        {
            VisiblePath = visiblePath ?? throw new ArgumentNullException(nameof(visiblePath));
            HoverTargetPath = hoverTargetPath ?? throw new ArgumentNullException(nameof(hoverTargetPath));
            HoverTargetPath.PointerEntered += HandleHoverTargetPointerEntered;
            HoverTargetPath.PointerExited += HandleHoverTargetPointerExited;
            HoverTargetPath.DetachedFromVisualTree += HandleHoverTargetDetachedFromVisualTree;
        }

        /// <summary>
        /// Gets the painted edge stroke path.
        /// </summary>
        public ShapePath VisiblePath { get; }

        /// <summary>
        /// Gets the wider transparent hover target path that shares the same geometry.
        /// </summary>
        public ShapePath HoverTargetPath { get; }

        /// <summary>
        /// Updates the retained edge visual from the latest edge view model and one shared geometry instance.
        /// </summary>
        public void Update(ExecutionEdgeViewModel edge)
        {
            if (edge == null)
            {
                throw new ArgumentNullException(nameof(edge));
            }

            /* Keep the geometry-materialization span separate from the later visual-property writes so traces can show
               whether the retained-edge hitch comes from geometry construction or from applying that geometry to paths. */
            Geometry sharedGeometry = edge.CreatePathGeometry();

            /* DataContext writes can trigger binding churn on retained paths, so isolate them from the geometry work and
               from the later path-data assignment. */
            using (PerformanceActivityScope dataContextActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.UpdateEdgeVisual.AssignDataContext"))
            {
                VisiblePath.DataContext = edge;
                HoverTargetPath.DataContext = edge;
            }

            /* Assigning the shared geometry back onto the two ShapePath instances is the next likely UI-thread cost once
               raw StreamGeometry construction is ruled out, so time that boundary independently. */
            using (PerformanceActivityScope geometryAssignmentActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.UpdateEdgeVisual.AssignPathData"))
            {
                VisiblePath.Data = sharedGeometry;
                HoverTargetPath.Data = sharedGeometry;
            }

            /* Target-task observation can rebalance subscriptions and status classes, so measure it separately from the
               raw path updates. */
            using (PerformanceActivityScope observeTaskActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.UpdateEdgeVisual.ObserveTargetTask"))
            {
                ObserveTargetTask(edge.Target.Task);
            }
        }

        /// <summary>
        /// Applies one shared z-index to both retained edge paths.
        /// </summary>
        public void SetZIndex(int zIndex)
        {
            VisiblePath.ZIndex = zIndex;
            HoverTargetPath.ZIndex = zIndex;
        }

        /// <summary>
        /// Releases the target-task subscription and hover handlers before the retained edge leaves the visual tree.
        /// </summary>
        public void Dispose()
        {
            HoverTargetPath.PointerEntered -= HandleHoverTargetPointerEntered;
            HoverTargetPath.PointerExited -= HandleHoverTargetPointerExited;
            HoverTargetPath.DetachedFromVisualTree -= HandleHoverTargetDetachedFromVisualTree;
            VisiblePath.Classes.Set("hover", false);
            DetachObservedTask();
        }

        /// <summary>
        /// Tracks the current target task so the visible edge classes stay synchronized with task status changes.
        /// </summary>
        private void ObserveTargetTask(ExecutionTaskViewModel task)
        {
            if (ReferenceEquals(_observedTask, task))
            {
                ApplyStatusClasses();
                return;
            }

            DetachObservedTask();
            _observedTask = task;
            _taskStatusChangedHandler = HandleObservedTaskPropertyChanged;
            _observedTask.PropertyChanged += _taskStatusChangedHandler;
            ApplyStatusClasses();
        }

        /// <summary>
        /// Removes the current target-task subscription when the retained edge changes identity or leaves the canvas.
        /// </summary>
        private void DetachObservedTask()
        {
            if (_observedTask == null || _taskStatusChangedHandler == null)
            {
                return;
            }

            _observedTask.PropertyChanged -= _taskStatusChangedHandler;
            _observedTask = null;
            _taskStatusChangedHandler = null;
        }

        /// <summary>
        /// Reapplies edge status classes when the target task changes the user-facing status used for tinting.
        /// </summary>
        private void HandleObservedTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName) ||
                string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.DisplayStatus), StringComparison.Ordinal))
            {
                ApplyStatusClasses();
            }
        }

        /// <summary>
        /// Applies the current target-task status classes to the visible edge path.
        /// </summary>
        private void ApplyStatusClasses()
        {
            if (_observedTask == null)
            {
                return;
            }

            ExecutionStatusClasses.ApplyStatusClasses(VisiblePath.Classes, _observedTask.DisplayStatus);
        }

        /// <summary>
        /// Turns on the visible edge hover styling when the transparent hit target receives the pointer.
        /// </summary>
        private void HandleHoverTargetPointerEntered(object? sender, PointerEventArgs e)
        {
            VisiblePath.Classes.Set("hover", true);
        }

        /// <summary>
        /// Clears the visible edge hover styling when the pointer leaves the transparent hit target.
        /// </summary>
        private void HandleHoverTargetPointerExited(object? sender, PointerEventArgs e)
        {
            VisiblePath.Classes.Set("hover", false);
        }

        /// <summary>
        /// Clears hover styling when the hit target detaches so reused paths never keep stale hover state.
        /// </summary>
        private void HandleHoverTargetDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            VisiblePath.Classes.Set("hover", false);
        }
    }

    /// <summary>
    /// Ends the active pan gesture and releases pointer capture.
    /// </summary>
    private void EndPanGesture(IPointer? pointer)
    {
        _isPanning = false;
        pointer?.Capture(null);
    }

    /// <summary>
    /// Converts one viewport-space point into the graph's world-space coordinates.
    /// </summary>
    private Point ViewportToWorld(Point viewportPoint)
    {
        double worldX = (_zoom == 0) ? viewportPoint.X : (viewportPoint.X - _panX) / _zoom;
        double worldY = (_zoom == 0) ? viewportPoint.Y : (viewportPoint.Y - _panY) / _zoom;
        return new Point(worldX, worldY);
    }

    /// <summary>
    /// Applies the current pan and zoom values to the graph content root.
    /// </summary>
    private void ApplyViewportTransform()
    {
        if (_graphContentRoot == null)
        {
            return;
        }

        TransformGroup transform = new()
        {
            Children =
            {
                new ScaleTransform(_zoom, _zoom),
                new TranslateTransform(_panX, _panY)
            }
        };
        _graphContentRoot.RenderTransform = transform;
    }

    /// <summary>
    /// Applies the pending first-fit or post-switch visibility recovery once the current viewport has its real post-layout
    /// size, instead of making that decision during an earlier render triggered from the previous tab layout.
    /// </summary>
    private void ApplyPendingViewportAdjustment()
    {
        if (!_pendingViewportAdjustment)
        {
            return;
        }

        if (!TryGetViewportMetrics(out ViewportMetrics metrics))
        {
            return;
        }

        if (!_hasViewportState)
        {
            FitGraphToViewportIfNeeded(metrics);
            _hasViewportState = true;
        }
        else
        {
            NudgeGraphIntoViewIfNeeded(metrics);
        }

        ApplyViewportTransform();
        _pendingViewportAdjustment = false;
    }

    /// <summary>
    /// Fits the current graph into the visible viewport the first time a layout is rendered.
    /// </summary>
    private void FitGraphToViewportIfNeeded(ViewportMetrics metrics)
    {
        if (Math.Abs(_zoom - DefaultZoom) > 0.001 || Math.Abs(_panX) > 0.001 || Math.Abs(_panY) > 0.001)
        {
            return;
        }

        double fitZoom = Math.Min(metrics.AvailableWidth / metrics.GraphWidth, metrics.AvailableHeight / metrics.GraphHeight);
        _zoom = Math.Clamp(fitZoom, MinZoom, DefaultZoom);
        _panX = Math.Max(ViewportFitMargin, (metrics.AvailableWidth - (metrics.GraphWidth * _zoom)) / 2.0 + ViewportFitMargin);
        _panY = Math.Max(ViewportFitMargin, (metrics.AvailableHeight - (metrics.GraphHeight * _zoom)) / 2.0 + ViewportFitMargin);
    }

    /// <summary>
    /// Nudges the graph back into view when the current pan has moved it mostly or completely outside the viewport. The
    /// current zoom level is preserved and only the minimum pan correction needed to restore a useful amount of visible
    /// graph area is applied.
    /// </summary>
    private void NudgeGraphIntoViewIfNeeded(ViewportMetrics metrics)
    {
        double graphWidth = metrics.GraphWidth * _zoom;
        double graphHeight = metrics.GraphHeight * _zoom;
        double graphLeft = _panX;
        double graphTop = _panY;
        double graphRight = graphLeft + graphWidth;
        double graphBottom = graphTop + graphHeight;
        double viewLeft = ViewportRecoveryMargin;
        double viewTop = ViewportRecoveryMargin;
        double viewRight = Math.Max(viewLeft, metrics.ViewportWidth - ViewportRecoveryMargin);
        double viewBottom = Math.Max(viewTop, metrics.ViewportHeight - ViewportRecoveryMargin);
        /* Recover more than a token sliver of the graph so tab switches bring the content back into a comfortably
           visible working area rather than leaving it clinging to the viewport edge. */
        double requiredVisibleWidth = Math.Min(graphWidth, Math.Max(220, (viewRight - viewLeft) * 0.35));
        double requiredVisibleHeight = Math.Min(graphHeight, Math.Max(140, (viewBottom - viewTop) * 0.3));

        double deltaX = CalculateRecoveryDelta(graphLeft, graphRight, viewLeft, viewRight, requiredVisibleWidth);
        double deltaY = CalculateRecoveryDelta(graphTop, graphBottom, viewTop, viewBottom, requiredVisibleHeight);

        if (Math.Abs(deltaX) > 0.001 || Math.Abs(deltaY) > 0.001)
        {
            _panX += deltaX;
            _panY += deltaY;
        }
    }

    /// <summary>
    /// Calculates the smallest translation needed to ensure that at least the requested amount of one axis remains
    /// visible inside the current viewport bounds.
    /// </summary>
    private static double CalculateRecoveryDelta(double graphStart, double graphEnd, double viewStart, double viewEnd, double requiredVisible)
    {
        double overlap = Math.Min(graphEnd, viewEnd) - Math.Max(graphStart, viewStart);
        if (overlap >= requiredVisible)
        {
            return 0;
        }

        if (graphEnd <= viewStart)
        {
            return (viewStart + requiredVisible) - graphEnd;
        }

        if (graphStart >= viewEnd)
        {
            return (viewEnd - requiredVisible) - graphStart;
        }

        if (graphStart < viewStart)
        {
            return (viewStart + requiredVisible) - graphEnd;
        }

        if (graphEnd > viewEnd)
        {
            return (viewEnd - requiredVisible) - graphStart;
        }

        return 0;
    }

    /// <summary>
    /// Returns the current graph and both visible canvas layers when the render surfaces are ready.
    /// </summary>
    private bool TryGetGraphSurfaces(out GraphSurfaces surfaces)
    {
        if (_observedGraph == null || _structureCanvas == null || _taskCanvas == null)
        {
            surfaces = default;
            return false;
        }

        surfaces = new GraphSurfaces(_observedGraph, _structureCanvas, _taskCanvas);
        return true;
    }

    /// <summary>
    /// Returns the current viewport and graph metrics used by the fit and recovery paths.
    /// </summary>
    private bool TryGetViewportMetrics(out ViewportMetrics metrics)
    {
        metrics = default;
        if (_viewportHost == null || _observedGraph == null)
        {
            return false;
        }

        if (_viewportHost.Bounds.Width <= 0 || _viewportHost.Bounds.Height <= 0)
        {
            return false;
        }

        metrics = new ViewportMetrics(
            _viewportHost.Bounds.Width,
            _viewportHost.Bounds.Height,
            Math.Max(1, _observedGraph.CanvasWidth),
            Math.Max(1, _observedGraph.CanvasHeight),
            Math.Max(1, _viewportHost.Bounds.Width - (ViewportFitMargin * 2)),
            Math.Max(1, _viewportHost.Bounds.Height - (ViewportFitMargin * 2)));
        return true;
    }

    /// <summary>
    /// Carries the current viewport and graph dimensions so fit and recovery logic can share one validated source.
    /// </summary>
    private readonly record struct ViewportMetrics(
        double ViewportWidth,
        double ViewportHeight,
        double GraphWidth,
        double GraphHeight,
        double AvailableWidth,
        double AvailableHeight);

    /// <summary>
    /// Carries the current graph plus all visible render surfaces once their null checks have been validated together.
    /// </summary>
    private readonly record struct GraphSurfaces(
        ExecutionGraphViewModel Graph,
        Canvas StructureCanvas,
        Canvas TaskCanvas);

    /// <summary>
    /// Returns whether the pointer press started from a rendered node control that should keep click-selection
    /// behavior instead of initiating a pan gesture.
    /// </summary>
    private static bool IsNodeInteractionSource(Control sourceControl)
    {
        foreach (Visual visual in sourceControl.GetSelfAndVisualAncestors())
        {
            if (visual is not Control control)
            {
                continue;
            }

            if (control.Classes.Contains("graph-interactive"))
            {
                return true;
            }
        }

        return false;
    }

}
