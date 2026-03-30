using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using LocalAutomation.Avalonia.ViewModels;

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

    private Canvas? _graphCanvas;
    private Border? _viewportHost;
    private Border? _graphContentRoot;
    private ExecutionGraphViewModel? _observedGraph;
    private bool _isPanning;
    private bool _needsInitialFit;
    private Point _lastPanPoint;
    private double _zoom = DefaultZoom;
    private double _panX;
    private double _panY;

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

        if (e.Source is Control sourceControl && IsLeafTaskInteractionSource(sourceControl))
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
        _needsInitialFit = false;
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
        _needsInitialFit = false;
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

        ResetViewport();
        _needsInitialFit = true;
        RenderGraph();
    }

    /// <summary>
    /// Applies the deferred first-fit pass once the control is attached and participating in layout.
    /// </summary>
    private void HandleAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        TryApplyInitialFit();
    }

    /// <summary>
    /// Retries the initial fit when the viewport receives its first real size after graph content has already rendered.
    /// </summary>
    private void HandleViewportHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        TryApplyInitialFit();
    }

    /// <summary>
    /// Re-renders the graph when the graph-node or graph-edge collections change.
    /// </summary>
    private void HandleGraphCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RenderGraph();
    }

    /// <summary>
    /// Re-renders the graph when layout-dependent graph properties change.
    /// </summary>
    private void HandleGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(ExecutionGraphViewModel.CanvasWidth), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionGraphViewModel.CanvasHeight), StringComparison.Ordinal))
        {
            RenderGraph();
        }
    }

    /// <summary>
    /// Rebuilds the graph canvas from the current graph view model so comment containers, edges, and task cards render
    /// in a deterministic back-to-front order.
    /// </summary>
    private void RenderGraph()
    {
        if (_graphCanvas == null)
        {
            return;
        }

        _graphCanvas.Children.Clear();
        if (_observedGraph == null)
        {
            return;
        }

        foreach (ExecutionNodeViewModel group in _observedGraph.Nodes.Where(node => node.IsContainer).OrderBy(node => node.Width * node.Height))
        {
            _graphCanvas.Children.Add(CreateGroupContainer(group));
        }

        foreach (ExecutionEdgeViewModel edge in _observedGraph.Edges)
        {
            global::Avalonia.Controls.Shapes.Path path = new()
            {
                Data = edge.PathData,
                Stroke = new SolidColorBrush(Color.Parse(edge.Stroke)),
                StrokeThickness = 2,
                Opacity = 0.72,
                IsHitTestVisible = false
            };
            _graphCanvas.Children.Add(path);
        }

        foreach (ExecutionNodeViewModel node in _observedGraph.Nodes.Where(node => !node.IsContainer))
        {
            _graphCanvas.Children.Add(CreateTaskCard(node));
        }

        foreach (ExecutionNodeViewModel group in _observedGraph.Nodes.Where(node => node.IsContainer))
        {
            _graphCanvas.Children.Add(CreateGroupHeader(group));
        }

        TryApplyInitialFit();
        ApplyViewportTransform();
    }

    /// <summary>
    /// Creates one comment-style group container behind its child graph nodes.
    /// </summary>
    private static Control CreateGroupContainer(ExecutionNodeViewModel group)
    {
        Border container = new()
        {
            Width = group.Width,
            Height = group.Height,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(group.IsSelected ? 2.0 : 1.5),
            BorderBrush = new SolidColorBrush(Color.Parse(group.StatusBrush)),
            Background = new SolidColorBrush(Color.Parse(group.IsSelected ? "#17212C" : "#121922")),
            Opacity = 0.94,
            DataContext = group
        };
        container.Classes.Add("execution-node-card");
        container.Classes.Add("group");
        if (group.IsSelected)
        {
            container.Classes.Add("selected");
        }

        Canvas.SetLeft(container, group.X);
        Canvas.SetTop(container, group.Y);
        return container;
    }

    /// <summary>
    /// Creates one clickable header strip for a group container.
    /// </summary>
    private Control CreateGroupHeader(ExecutionNodeViewModel group)
    {
        Border header = new()
        {
            Width = group.Width,
            Height = ExecutionGraphViewModel.GroupHeaderHeight,
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            Background = new SolidColorBrush(Color.Parse(group.IsSelected ? "#1A2633" : "#16202B")),
            BorderBrush = new SolidColorBrush(Color.Parse(group.StatusBrush)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            DataContext = group
        };

        Canvas.SetLeft(header, group.X);
        Canvas.SetTop(header, group.Y);

        Button button = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            DataContext = group,
            Content = CreateGroupHeaderContent(group)
        };
        button.Click += ExecutionNode_Click;
        header.Child = button;
        return header;
    }

    /// <summary>
    /// Creates one positioned card for a runnable task.
    /// </summary>
    private Control CreateTaskCard(ExecutionNodeViewModel node)
    {
        Border card = new()
        {
            Width = node.Width,
            Height = node.Height,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.Parse(node.StatusBrush)),
            Background = new SolidColorBrush(Color.Parse("#181E27")),
            DataContext = node
        };
        card.Classes.Add("execution-node-card");
        if (node.IsSelected)
        {
            card.Classes.Add("selected");
        }

        Canvas.SetLeft(card, node.X);
        Canvas.SetTop(card, node.Y);

        Button button = new()
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            DataContext = node,
            Content = CreateTaskCardContent(node)
        };
        button.Click += ExecutionNode_Click;
        card.Child = button;
        return card;
    }

    /// <summary>
    /// Creates the inner content for one group header.
    /// </summary>
    private static Control CreateGroupHeaderContent(ExecutionNodeViewModel group)
    {
        Grid root = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(14, 8, 14, 8)
        };

        StackPanel textColumn = new()
        {
            Spacing = 2
        };
        textColumn.Children.Add(new TextBlock
        {
            Text = group.Title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        textColumn.Children.Add(new TextBlock
        {
            Text = group.SummaryText,
            TextWrapping = TextWrapping.Wrap
        });
        textColumn.Classes.Add("muted");
        root.Children.Add(textColumn);

        Border statusPill = new()
        {
            Background = new SolidColorBrush(Color.Parse(group.StatusBrush)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = group.StatusText,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#0F1115"))
            }
        };
        Grid.SetColumn(statusPill, 1);
        root.Children.Add(statusPill);
        return root;
    }

    /// <summary>
    /// Creates the inner content for one runnable-task card.
    /// </summary>
    private static Control CreateTaskCardContent(ExecutionNodeViewModel node)
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(12, 10, 12, 10)
        };

        Grid titleRow = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        titleRow.Children.Add(new TextBlock
        {
            Text = node.Title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });

        Border statusPill = new()
        {
            Background = new SolidColorBrush(Color.Parse(node.StatusBrush)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(6, 2),
            Child = new TextBlock
            {
                Text = node.StatusText,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.Parse("#0F1115"))
            }
        };
        Grid.SetColumn(statusPill, 1);
        titleRow.Children.Add(statusPill);
        root.Children.Add(titleRow);

        TextBlock description = new()
        {
            Text = node.Description,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 34,
            Margin = new Thickness(0, 8, 0, 0)
        };
        description.Classes.Add("muted");
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        TextBlock id = new()
        {
            Text = node.Id,
            Margin = new Thickness(0, 8, 0, 0)
        };
        id.Classes.Add("detail-label");
        id.Classes.Add("mono");
        Grid.SetRow(id, 2);
        root.Children.Add(id);
        return root;
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
        ApplyViewportTransform();
    }

    /// <summary>
    /// Applies the initial fit once after graph content and viewport bounds are both available.
    /// </summary>
    private void TryApplyInitialFit()
    {
        if (!_needsInitialFit)
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

        FitGraphToViewportIfNeeded();
        ApplyViewportTransform();
        _needsInitialFit = false;
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
    /// Returns whether the pointer press started from a runnable-task control that should keep click-selection
    /// behavior instead of initiating a pan gesture.
    /// </summary>
    private static bool IsLeafTaskInteractionSource(Control sourceControl)
    {
        foreach (Visual visual in sourceControl.GetSelfAndVisualAncestors())
        {
            if (visual is not Control control)
            {
                continue;
            }

            if (control.DataContext is not ExecutionNodeViewModel node)
            {
                continue;
            }

            return !node.IsGroup || control is Button;
        }

        return false;
    }
}
