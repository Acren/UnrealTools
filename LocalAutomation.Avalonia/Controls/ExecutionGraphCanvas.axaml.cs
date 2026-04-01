using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Collections.Generic;
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

    private Canvas? _graphCanvas;
    private Border? _viewportHost;
    private Border? _graphContentRoot;
    private ExecutionGraphViewModel? _observedGraph;
    private readonly Dictionary<RuntimeExecutionTaskId, Control> _renderedNodeControls = new();
    private bool _isPanning;
    private bool _pendingRender;
    private string _pendingRenderTrigger = "Deferred";
    private bool _pendingViewportAdjustment;
    private bool _measurementRenderPending;
    private Point _lastPanPoint;
    private double _zoom = DefaultZoom;
    private double _panX;
    private double _panY;
    private bool _hasViewportState;

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
        _graphCanvas = this.FindControl<Canvas>("GraphCanvas");
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
        if (_observedGraph != null)
        {
            _observedGraph.Nodes.CollectionChanged -= HandleGraphCollectionChanged;
            _observedGraph.Edges.CollectionChanged -= HandleGraphCollectionChanged;
            _observedGraph.PropertyChanged -= HandleGraphPropertyChanged;
        }

        _observedGraph = DataContext as ExecutionGraphViewModel;
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
        RenderGraph(trigger: "DataContextChanged");

        if (_viewportHost != null && _viewportHost.Bounds.Width > 0 && _viewportHost.Bounds.Height > 0)
        {
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
        if (string.Equals(e.PropertyName, nameof(ExecutionGraphViewModel.IsUpdatingGraph), StringComparison.Ordinal))
        {
            if (_observedGraph?.IsUpdatingGraph == false && _pendingRender)
            {
                string pendingTrigger = _pendingRenderTrigger;
                _pendingRender = false;
                _pendingRenderTrigger = "Deferred";
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

        _pendingRender = true;
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

        if (_graphCanvas == null)
        {
            activity.SetTag("render.skipped", "MissingCanvas");
            return;
        }

        _graphCanvas.Children.Clear();
        _renderedNodeControls.Clear();
        if (_observedGraph == null)
        {
            activity.SetTag("render.skipped", "MissingGraph");
            return;
        }

        int groupCount = _observedGraph.Nodes.Count(node => node.IsContainer);
        int leafCount = _observedGraph.Nodes.Count - groupCount;
        activity.SetTag("group.count", groupCount)
            .SetTag("leaf.count", leafCount)
            .SetTag("edge.count", _observedGraph.Edges.Count);

        foreach (ExecutionNodeViewModel group in _observedGraph.Nodes.Where(node => node.IsContainer).OrderByDescending(node => node.Width * node.Height))
        {
            Control groupControl = CreateGroupControl(group);
            _renderedNodeControls[group.Id] = groupControl;
            _graphCanvas.Children.Add(groupControl);
        }

        foreach (ExecutionEdgeViewModel edge in _observedGraph.Edges)
        {
            ShapePath path = new()
            {
                StrokeThickness = 2,
                Opacity = 0.72,
                IsHitTestVisible = false,
                DataContext = edge
            };

            /* Bind edge geometry while semantic classes drive tinting from XAML, so the edge uses the same status
               language as the rest of the execution graph without hard-coded color values in view models. */
            path.Classes.Add("execution-edge");
            ApplyEdgeStatusClasses(path, edge.Target);
            BindToDataContext(path, ShapePath.DataProperty, nameof(ExecutionEdgeViewModel.PathData));
            _graphCanvas.Children.Add(path);
        }

        foreach (ExecutionNodeViewModel node in _observedGraph.Nodes.Where(node => !node.IsContainer))
        {
            Control taskControl = CreateTaskControl(node);
            _renderedNodeControls[node.Id] = taskControl;
            _graphCanvas.Children.Add(taskControl);
        }

        ApplyViewportTransform();
        activity.SetTag("render.child.count", _graphCanvas.Children.Count);

        if (!_measurementRenderPending)
        {
            _measurementRenderPending = true;
            Dispatcher.UIThread.Post(() => ApplyMeasuredNodeLayout(trigger), DispatcherPriority.Render);
        }
    }

    /// <summary>
    /// Re-measures the actual rendered controls after Avalonia layout so graph bounds can follow real on-screen widths
    /// instead of detached control guesses.
     /// </summary>
    private void ApplyMeasuredNodeLayout(string trigger)
    {
        _measurementRenderPending = false;
        if (_observedGraph == null || _renderedNodeControls.Count == 0)
        {
            return;
        }

        bool anyWidthChanged = UpdateMeasuredNodeWidths(_observedGraph);
        if (!anyWidthChanged)
        {
            return;
        }

        _observedGraph.Relayout();
        RenderGraph(trigger: $"MeasuredActual:{trigger}");
    }

    /// <summary>
    /// Reads the actual rendered widths from the graph controls and updates the graph VM cache when those widths change.
    /// </summary>
    private bool UpdateMeasuredNodeWidths(ExecutionGraphViewModel graph)
    {
        bool anyWidthChanged = false;
        foreach (ExecutionNodeViewModel node in graph.Nodes)
        {
            if (!_renderedNodeControls.TryGetValue(node.Id, out Control? control))
            {
                continue;
            }

            double measuredWidth = control is ExecutionGroupContainer groupControl
                ? groupControl.GetHeaderContentWidth()
                : Math.Max(1, control.Bounds.Width);
            if (graph.SetMeasuredNodeWidth(node.Id, measuredWidth))
            {
                anyWidthChanged = true;
            }
        }

        return anyWidthChanged;
    }

    /// <summary>
     /// Creates one XAML-backed group control and positions it on the graph canvas.
     /// </summary>
    private Control CreateGroupControl(ExecutionNodeViewModel group)
    {
        ExecutionGroupContainer container = new()
        {
            GroupWidth = group.Width,
            GroupHeight = group.Height,
            HeaderHeight = ExecutionGraphViewModel.GroupHeaderHeight,
            DataContext = group,
        };

        container.Invoked += ExecutionNode_Click;

        Canvas.SetLeft(container, group.X);
        Canvas.SetTop(container, group.Y);
        return container;
    }

    /// <summary>
     /// Creates one XAML-backed task-card control and positions it on the graph canvas.
     /// </summary>
    private Control CreateTaskControl(ExecutionNodeViewModel node)
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
    /// Applies the current target-node status classes to an edge path and keeps them synchronized while the target node
    /// status changes, so edge tinting can stay fully style-driven in XAML.
    /// </summary>
    private static void ApplyEdgeStatusClasses(ShapePath path, ExecutionNodeViewModel target)
    {
        ExecutionStatusClasses.ApplyStatusClasses(path.Classes, target.Status);

        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.PropertyName) ||
                string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.Status), StringComparison.Ordinal))
            {
                ExecutionStatusClasses.ApplyStatusClasses(path.Classes, target.Status);
            }
        };

        target.PropertyChanged += handler;
        path.DetachedFromVisualTree += (_, _) => target.PropertyChanged -= handler;
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
    /// Resets the viewport transform to its default state before the next graph fit occurs.
    /// </summary>
    private void ResetViewport()
    {
        _zoom = DefaultZoom;
        _panX = 0;
        _panY = 0;
        _hasViewportState = false;
        ApplyViewportTransform();
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

        if (_viewportHost == null || _observedGraph == null)
        {
            return;
        }

        if (_viewportHost.Bounds.Width <= 0 || _viewportHost.Bounds.Height <= 0)
        {
            return;
        }

        if (!_hasViewportState)
        {
            FitGraphToViewportIfNeeded();
            _hasViewportState = true;
        }
        else
        {
            NudgeGraphIntoViewIfNeeded();
        }

        ApplyViewportTransform();
        _pendingViewportAdjustment = false;
    }

    /// <summary>
    /// Fits the current graph into the visible viewport the first time a layout is rendered.
    /// </summary>
    private void FitGraphToViewportIfNeeded()
    {
        if (_viewportHost == null || _observedGraph == null)
        {
            return;
        }

        if (_viewportHost.Bounds.Width <= 0 || _viewportHost.Bounds.Height <= 0)
        {
            return;
        }

        if (Math.Abs(_zoom - DefaultZoom) > 0.001 || Math.Abs(_panX) > 0.001 || Math.Abs(_panY) > 0.001)
        {
            return;
        }

        double graphWidth = Math.Max(1, _observedGraph.CanvasWidth);
        double graphHeight = Math.Max(1, _observedGraph.CanvasHeight);
        double availableWidth = Math.Max(1, _viewportHost.Bounds.Width - 24);
        double availableHeight = Math.Max(1, _viewportHost.Bounds.Height - 24);
        double fitZoom = Math.Min(availableWidth / graphWidth, availableHeight / graphHeight);
        _zoom = Math.Clamp(fitZoom, MinZoom, DefaultZoom);
        _panX = Math.Max(12, (availableWidth - (graphWidth * _zoom)) / 2.0 + 12);
        _panY = Math.Max(12, (availableHeight - (graphHeight * _zoom)) / 2.0 + 12);
    }

    /// <summary>
    /// Nudges the graph back into view when the current pan has moved it mostly or completely outside the viewport. The
    /// current zoom level is preserved and only the minimum pan correction needed to restore a useful amount of visible
    /// graph area is applied.
    /// </summary>
    private void NudgeGraphIntoViewIfNeeded()
    {
        if (_viewportHost == null || _observedGraph == null)
        {
            return;
        }

        if (_viewportHost.Bounds.Width <= 0 || _viewportHost.Bounds.Height <= 0)
        {
            return;
        }

        double graphWidth = Math.Max(1, _observedGraph.CanvasWidth * _zoom);
        double graphHeight = Math.Max(1, _observedGraph.CanvasHeight * _zoom);
        double graphLeft = _panX;
        double graphTop = _panY;
        double graphRight = graphLeft + graphWidth;
        double graphBottom = graphTop + graphHeight;
        double viewLeft = ViewportRecoveryMargin;
        double viewTop = ViewportRecoveryMargin;
        double viewRight = Math.Max(viewLeft, _viewportHost.Bounds.Width - ViewportRecoveryMargin);
        double viewBottom = Math.Max(viewTop, _viewportHost.Bounds.Height - ViewportRecoveryMargin);
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
