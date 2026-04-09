using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
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
    private const double DefaultZoom = 1.0;
    private const double MinZoom = 0.35;
    private const double MaxZoom = 2.5;
    private const double ZoomStep = 1.15;
    private const double ViewportRecoveryMargin = 40;
    private const double ViewportFitMargin = 12;
    private const double EdgeHitThickness = 10;
    private const int ReconciliationInstrumentationInterval = 10;

    private Canvas? _structureCanvas;
    private Canvas? _taskCanvas;
    private Canvas? _measurementCanvas;
    private Border? _viewportHost;
    private Border? _graphContentRoot;
    private ExecutionGraphViewModel? _observedGraph;
    private readonly Dictionary<RuntimeExecutionTaskId, ExecutionGroupContainer> _renderedGroupControls = new();
    private readonly Dictionary<RuntimeExecutionTaskId, ExecutionTaskCard> _renderedTaskControls = new();
    private readonly Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId, bool IsHoverTarget), ShapePath> _renderedEdgePaths = new();
    private readonly Dictionary<RuntimeExecutionTaskId, Control> _measurementNodeControls = new();
    private readonly Dictionary<RuntimeExecutionTaskId, double> _pendingGroupWidthUpdates = new();
    private bool _isPanning;
    private string? _pendingRenderTrigger;
    private bool _measurementRefreshPending;
    private string? _pendingMeasurementTrigger;
    private bool _pendingViewportAdjustment;
    private GroupWidthUpdatePhase _groupWidthUpdatePhase;
    private bool _suppressGraphNotifications;
    private Point _lastPanPoint;
    private double _zoom = DefaultZoom;
    private double _panX;
    private double _panY;
    private bool _hasViewportState;
    private int _reconciliationCount;

    /// <summary>
    /// Represents the current lifecycle of the deferred visible-group width update queue.
    /// </summary>
    private enum GroupWidthUpdatePhase
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
        _measurementCanvas = this.FindControl<Canvas>("MeasurementCanvas");
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
            .SetTag("pending.render", !string.IsNullOrWhiteSpace(_pendingRenderTrigger))
            .SetTag("measurement.pending", _measurementRefreshPending);

        if (_observedGraph != null)
        {
            _observedGraph.Nodes.CollectionChanged -= HandleGraphCollectionChanged;
            _observedGraph.Edges.CollectionChanged -= HandleGraphCollectionChanged;
            _observedGraph.PropertyChanged -= HandleGraphPropertyChanged;
        }

        _observedGraph = DataContext as ExecutionGraphViewModel;
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
        /* The pre-measure path mutates graph layout state before the visible render occurs. Ignore those intermediate
           notifications so the canvas renders once from the final measured layout instead of recursively re-entering.
        */
        if (_suppressGraphNotifications)
        {
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ExecutionGraphViewModel.IsUpdatingGraph), StringComparison.Ordinal))
        {
            if (_observedGraph?.IsUpdatingGraph == false && !string.IsNullOrWhiteSpace(_pendingRenderTrigger))
            {
                string pendingTrigger = _pendingRenderTrigger;
                _pendingRenderTrigger = null;
                RenderGraph(trigger: $"Deferred:{pendingTrigger}");
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

        _pendingRenderTrigger = trigger;
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

        if (_structureCanvas == null || _taskCanvas == null || _measurementCanvas == null)
        {
            activity.SetTag("render.skipped", "MissingCanvas");
            return;
        }

        if (_observedGraph == null)
        {
            activity.SetTag("render.skipped", "MissingGraph");
            return;
        }

        if (_measurementRefreshPending)
        {
            activity.SetTag("render.deferred", "MeasurementPending");
            return;
        }

        BeginMeasuredRefresh(trigger, _observedGraph);
    }

    /// <summary>
    /// Populates the hidden attached measurement host and posts one completion callback after Avalonia has had a render
    /// turn to realize natural control sizes.
    /// </summary>
    private void BeginMeasuredRefresh(string trigger, ExecutionGraphViewModel graph)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.BeginMeasuredRefresh")
            .SetTag("trigger", trigger);
        if (_measurementCanvas == null)
        {
            activity.SetTag("refresh.skipped", "MissingMeasurementCanvas");
            return;
        }

        IReadOnlyList<ExecutionNodeViewModel> measuredLeaves;
        using (PerformanceActivityScope checkMeasurementNeedActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.CheckMeasurementNeed"))
        {
            measuredLeaves = GetLeavesNeedingMeasurement(graph);
            checkMeasurementNeedActivity.SetTag("measured.leaf.count", measuredLeaves.Count)
                .SetTag("visible.node.count", graph.Nodes.Count);
        }

        activity.SetTag("measured.leaf.count", measuredLeaves.Count)
            .SetTag("visible.node.count", graph.Nodes.Count);

        if (measuredLeaves.Count == 0)
        {
            _pendingMeasurementTrigger = trigger;
            using (PerformanceActivityScope inlineCompleteActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.CompleteMeasuredRefreshInline"))
            {
                CompleteMeasuredRefresh();
            }

            return;
        }

        _measurementRefreshPending = true;
        _pendingMeasurementTrigger = trigger;
        using (PerformanceActivityScope populateMeasurementHostActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.PopulateMeasurementHost"))
        {
            PopulateMeasurementHost(_measurementCanvas, measuredLeaves);
            populateMeasurementHostActivity.SetTag("measured.leaf.count", measuredLeaves.Count)
                .SetTag("measurement.control.count", _measurementNodeControls.Count);
        }

        using (PerformanceActivityScope postMeasurementRefreshActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.PostMeasurementRefresh"))
        {
            Dispatcher.UIThread.Post(CompleteMeasuredRefresh, DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Finalizes one graph refresh by reading widths from the attached hidden controls, relaying out the graph, clearing
    /// the measurement host, and then rebuilding the visible canvas once from the settled bounds.
    /// </summary>
    private void CompleteMeasuredRefresh()
    {
        if (!_measurementRefreshPending && _pendingMeasurementTrigger == null)
        {
            return;
        }

        if (!TryGetGraphSurfaces(out GraphSurfaces surfaces))
        {
            _measurementRefreshPending = false;
            _pendingMeasurementTrigger = null;
            return;
        }

        _measurementRefreshPending = false;
        string trigger = _pendingMeasurementTrigger ?? "Deferred";
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.CompleteMeasuredRefresh")
            .SetTag("trigger", trigger);

        bool widthsChanged;
        using (PerformanceActivityScope widthActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ApplyMeasuredNodeWidths"))
        {
            widthsChanged = ApplyMeasuredNodeWidths(surfaces.Graph);
            widthActivity.SetTag("widths.changed", widthsChanged);
        }

        activity.SetTag("widths.changed", widthsChanged);

        using (PerformanceActivityScope clearMeasurementActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.ClearMeasurementHost"))
        {
            ClearMeasurementHost(surfaces.MeasurementCanvas);
            clearMeasurementActivity.SetTag("measured.node.count", _measurementNodeControls.Count);
        }

        _pendingMeasurementTrigger = null;

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
    /// Reconciles retained group/task controls and rebuilds the edge layer from the current graph snapshot.
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

        int edgePathCount;
        using (PerformanceActivityScope edgeActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.RebuildEdgeLayer"))
        {
            edgePathCount = RebuildEdgeLayer(graph);
            edgeActivity.SetTag("edge.path.count", edgePathCount);
        }

        SetReconciliationSummaryTags(parentActivity, groupStats, taskStats, edgePathCount);
        SetReconciliationSummaryTags(activity, groupStats, taskStats, edgePathCount);
        SetReconciliationSummaryTags(sampledReconciliationActivity, groupStats, taskStats, edgePathCount);
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

            DetachGroupMeasuredWidthObserver(groupControl);
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

            DetachGroupMeasuredWidthObserver(staleGroupControl);
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
        int edgePathCount)
    {
        SetReconciliationTags(activity, prefix: "group", groupStats);
        SetReconciliationTags(activity, prefix: "task", taskStats);
        activity.SetTag("edge.path.count", edgePathCount);
    }

    /// <summary>
    /// Rebuilds the retained edge controls inside the shared structural canvas while leaving retained node controls and
    /// task cards mounted in place.
    /// </summary>
    private int RebuildEdgeLayer(ExecutionGraphViewModel graph)
    {
        if (_structureCanvas == null)
        {
            return 0;
        }

        foreach (ShapePath edgePath in _renderedEdgePaths.Values)
        {
            _structureCanvas.Children.Remove(edgePath);
        }

        _renderedEdgePaths.Clear();
        int edgePathCount = 0;
        foreach (ExecutionEdgeViewModel edge in graph.Edges)
        {
            /* Each rendered edge gets a visible path plus a wider transparent hit target so hover affordances are usable
               without forcing the user to land precisely on a two-pixel line. */
            foreach (((RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId, bool IsHoverTarget) key, ShapePath edgePath) in CreateEdgeControls(edge))
            {
                _renderedEdgePaths[key] = edgePath;
                _structureCanvas.Children.Add(edgePath);
                edgePathCount++;
            }
        }

        ApplyStructureZOrder(graph);
        return edgePathCount;
    }

    /// <summary>
    /// Creates the visible edge path plus a transparent hover target that toggles the same visual path classes.
    /// </summary>
    private static IEnumerable<((RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId, bool IsHoverTarget) key, ShapePath path)> CreateEdgeControls(ExecutionEdgeViewModel edge)
    {
        ShapePath visiblePath = new()
        {
            DataContext = edge
        };

        /* Bind edge geometry while semantic classes drive tinting from XAML, so the edge uses the same status language
           as the rest of the execution graph without hard-coded color values in view models. */
        visiblePath.Classes.Add("execution-edge");
        ApplyEdgeStatusClasses(visiblePath, edge.Target);
        BindToDataContext(visiblePath, ShapePath.DataProperty, nameof(ExecutionEdgeViewModel.PathData));

        ShapePath hoverTargetPath = new()
        {
            DataContext = edge,
            Stroke = Brushes.Transparent,
            StrokeThickness = EdgeHitThickness,
            IsHitTestVisible = true
        };

        /* The hover target shares the same geometry as the visible path but never paints anything, which makes the edge
           easy to interact with while preserving the slimmer rendered line. */
        BindToDataContext(hoverTargetPath, ShapePath.DataProperty, nameof(ExecutionEdgeViewModel.PathData));
        hoverTargetPath.PointerEntered += (_, _) => visiblePath.Classes.Set("hover", true);
        hoverTargetPath.PointerExited += (_, _) => visiblePath.Classes.Set("hover", false);
        hoverTargetPath.DetachedFromVisualTree += (_, _) => visiblePath.Classes.Set("hover", false);

        return
        [
            ((edge.Source.Id, edge.Target.Id, false), visiblePath),
            ((edge.Source.Id, edge.Target.Id, true), hoverTargetPath)
        ];
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
            if (_renderedEdgePaths.TryGetValue((edge.Source.Id, edge.Target.Id, false), out ShapePath? visiblePath))
            {
                visiblePath.ZIndex = resolvedEdgeZ;
            }

            if (_renderedEdgePaths.TryGetValue((edge.Source.Id, edge.Target.Id, true), out ShapePath? hoverPath))
            {
                hoverPath.ZIndex = resolvedEdgeZ;
            }
        }
    }

    /// <summary>
    /// Populates the hidden attached host with the current node controls so Avalonia can compute natural widths using the
    /// same visual tree and bindings as the final visible graph.
    /// </summary>
    private void PopulateMeasurementHost(Canvas measurementCanvas, IReadOnlyList<ExecutionNodeViewModel> measuredLeaves)
    {
        measurementCanvas.Children.Clear();
        _measurementNodeControls.Clear();

        foreach (ExecutionNodeViewModel node in measuredLeaves)
        {
            /* The layout layer only reuses detached width measurement for leaf cards. Visible group containers report
               live header widths through their retained controls instead of entering the hidden measurement host. */
            Control control = CreateTaskControl(node);
            _measurementNodeControls[node.Id] = control;
            measurementCanvas.Children.Add(control);
        }
    }

    /// <summary>
    /// Returns the current visible leaf nodes that still need one detached measurement pass before layout can trust their width.
    /// </summary>
    private static IReadOnlyList<ExecutionNodeViewModel> GetLeavesNeedingMeasurement(ExecutionGraphViewModel graph)
    {
        return graph.Nodes
            .Where(node => !node.IsContainer && graph.NeedsMeasuredNodeWidth(node.Id))
            .ToList();
    }

    /// <summary>
    /// Applies measured widths from the attached hidden controls and relays out the graph before the visible canvas is
    /// rebuilt.
    /// </summary>
    private bool ApplyMeasuredNodeWidths(ExecutionGraphViewModel graph)
    {
        bool anyWidthChanged = false;
        foreach (ExecutionNodeViewModel node in graph.Nodes)
        {
            if (!_measurementNodeControls.TryGetValue(node.Id, out Control? control))
            {
                continue;
            }

            double measuredWidth = MeasureNodeWidth(control);
            if (graph.SetMeasuredNodeWidth(node.Id, measuredWidth))
            {
                anyWidthChanged = true;
            }
        }

        if (!anyWidthChanged)
        {
            return false;
        }

        _suppressGraphNotifications = true;
        try
        {
            graph.Relayout();
        }
        finally
        {
            _suppressGraphNotifications = false;
        }

        return true;
    }

    /// <summary>
    /// Clears the hidden measurement host once all widths have been captured so detached controls do not linger in the
    /// visual tree between graph refreshes.
    /// </summary>
    private void ClearMeasurementHost(Canvas measurementCanvas)
    {
        measurementCanvas.Children.Clear();
        _measurementNodeControls.Clear();
    }

    /// <summary>
    /// Reads the natural width of one attached control from the hidden measurement host so final graph layout matches the
    /// actual control width that Avalonia produced from the XAML-owned node layout.
    /// </summary>
    private static double MeasureNodeWidth(Control control)
    {
        return Math.Max(1, control.Bounds.Width);
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
        AttachGroupMeasuredWidthObserver(container);

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
    /// Attaches one retained group control to the control-owned measured-width property so visible geometry drives graph
    /// width updates without a custom event contract.
    /// </summary>
    private void AttachGroupMeasuredWidthObserver(ExecutionGroupContainer container)
    {
        ((System.ComponentModel.INotifyPropertyChanged)container).PropertyChanged += HandleGroupControlPropertyChanged;
    }

    /// <summary>
    /// Removes the measured-width observer for a group control leaving the retained layer.
    /// </summary>
    private void DetachGroupMeasuredWidthObserver(ExecutionGroupContainer container)
    {
        ((System.ComponentModel.INotifyPropertyChanged)container).PropertyChanged -= HandleGroupControlPropertyChanged;
    }

    /// <summary>
    /// Observes control-owned measured width changes from retained group containers and forwards them into the coalesced
    /// graph width update queue.
    /// </summary>
    private void HandleGroupControlPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ExecutionGroupContainer.MeasuredHeaderMinWidth), StringComparison.Ordinal) ||
            sender is not ExecutionGroupContainer container ||
            container.DataContext is not ExecutionNodeViewModel node)
        {
            return;
        }

        EnqueuePendingGroupWidthUpdate(node.Id, container.MeasuredHeaderMinWidth);
    }

    /// <summary>
    /// Queues one visible group-container width update so the graph can relayout once per burst instead of once per
    /// control size change.
    /// </summary>
    private void EnqueuePendingGroupWidthUpdate(RuntimeExecutionTaskId taskId, double measuredWidth)
    {
        if (_observedGraph == null || _groupWidthUpdatePhase == GroupWidthUpdatePhase.Applying)
        {
            return;
        }

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.EnqueuePendingGroupWidthUpdate")
            .SetTag("task.id", taskId.Value)
            .SetTag("reported.width", measuredWidth.ToString("0.###"));

        _pendingGroupWidthUpdates[taskId] = measuredWidth;
        activity.SetTag("pending.group.width.count", _pendingGroupWidthUpdates.Count)
            .SetTag("flush.already.pending", _groupWidthUpdatePhase == GroupWidthUpdatePhase.Scheduled);
        if (_groupWidthUpdatePhase != GroupWidthUpdatePhase.Idle)
        {
            return;
        }

        _groupWidthUpdatePhase = GroupWidthUpdatePhase.Scheduled;
        Dispatcher.UIThread.Post(FlushPendingGroupWidthUpdates, DispatcherPriority.Render);
    }

    /// <summary>
    /// Applies one coalesced batch of visible group width updates through the existing graph width cache and relayout path.
    /// </summary>
    private void FlushPendingGroupWidthUpdates()
    {
        if (_observedGraph == null || _pendingGroupWidthUpdates.Count == 0)
        {
            _groupWidthUpdatePhase = GroupWidthUpdatePhase.Idle;
            return;
        }

        if (_measurementRefreshPending)
        {
            _groupWidthUpdatePhase = GroupWidthUpdatePhase.Scheduled;
            Dispatcher.UIThread.Post(FlushPendingGroupWidthUpdates, DispatcherPriority.Render);
            return;
        }

        _groupWidthUpdatePhase = GroupWidthUpdatePhase.Applying;

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.FlushPendingGroupWidthUpdates")
            .SetTag("pending.group.width.count", _pendingGroupWidthUpdates.Count);
        KeyValuePair<RuntimeExecutionTaskId, double>[] updates = _pendingGroupWidthUpdates.ToArray();
        _pendingGroupWidthUpdates.Clear();

        int changedWidthCount = 0;
        foreach ((RuntimeExecutionTaskId taskId, double width) in updates)
        {
            bool changed = _observedGraph.SetMeasuredNodeWidth(taskId, width);
            if (changed)
            {
                changedWidthCount++;
            }
        }

        activity.SetTag("group.width.changed.count", changedWidthCount);
        if (changedWidthCount == 0)
        {
            return;
        }

        _suppressGraphNotifications = true;
        try
        {
            _observedGraph.Relayout();
            RenderGraph(trigger: "Deferred:GroupWidthChanged");
        }
        finally
        {
            _suppressGraphNotifications = false;
            _groupWidthUpdatePhase = GroupWidthUpdatePhase.Idle;
        }
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
    /// Applies the current target-node semantic status classes to an edge path and keeps them synchronized while the node
    /// updates, so edge tinting follows user-facing outcome while animation still reads lifecycle elsewhere.
    /// </summary>
    private static void ApplyEdgeStatusClasses(ShapePath path, ExecutionNodeViewModel target)
    {
        ExecutionTaskViewModel task = target.Task;
        ExecutionStatusClasses.ApplyStatusClasses(path.Classes, task.DisplayStatus);

        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName) ||
                string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.DisplayStatus), StringComparison.Ordinal))
            {
                ExecutionStatusClasses.ApplyStatusClasses(path.Classes, task.DisplayStatus);
            }
        };

        task.PropertyChanged += handler;
        path.DetachedFromVisualTree += (_, _) => task.PropertyChanged -= handler;
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
    /// Returns the current graph and all three canvas layers when the visible render surfaces are ready.
    /// </summary>
    private bool TryGetGraphSurfaces(out GraphSurfaces surfaces)
    {
        if (_observedGraph == null || _structureCanvas == null || _taskCanvas == null || _measurementCanvas == null)
        {
            surfaces = default;
            return false;
        }

        surfaces = new GraphSurfaces(_observedGraph, _structureCanvas, _taskCanvas, _measurementCanvas);
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
        Canvas TaskCanvas,
        Canvas MeasurementCanvas);

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

    /// <summary>
    /// Binds one generated control property to its current data context so the direct-code canvas stays live without a
    /// full control rebuild for simple visual updates.
    /// </summary>
    private static void BindToDataContext(AvaloniaObject target, AvaloniaProperty property, string propertyName)
    {
        target.Bind(property, new Binding(propertyName));
    }
}
