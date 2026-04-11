using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Provides the shared execution-graph node behavior used by both task cards and group containers: pointer interaction,
/// selection-aware frame thickness, task/node subscription lifetime, and intrinsic-width reporting from real visible
/// content hosted inside an <see cref="IntrinsicSizeHost"/>.
/// </summary>
public abstract class ExecutionGraphNodeControlBase : UserControl
{
    private static readonly Thickness SelectedBorderThickness = new(2.0);
    private static readonly Thickness DefaultBorderThickness = new(1.5);

    private bool _isHovered;
    private bool _isPressed;
    private bool _subscriptionsAttached;
    private double _intrinsicNodeWidth = ExecutionGraphLayoutSettings.NodeMinWidth;
    private Border? _clickSurface;
    private IntrinsicSizeHost? _intrinsicSizeHost;
    private ExecutionNodeViewModel? _observedNode;

    /// <summary>
    /// Identifies the current border thickness used by the node frame.
    /// </summary>
    public static readonly DirectProperty<ExecutionGraphNodeControlBase, Thickness> FrameBorderThicknessProperty =
        AvaloniaProperty.RegisterDirect<ExecutionGraphNodeControlBase, Thickness>(
            nameof(FrameBorderThickness),
            control => control.FrameBorderThickness);

    /// <summary>
    /// Identifies the node's current intrinsic width resolved from its real visible content.
    /// </summary>
    public static readonly DirectProperty<ExecutionGraphNodeControlBase, double> IntrinsicNodeWidthProperty =
        AvaloniaProperty.RegisterDirect<ExecutionGraphNodeControlBase, double>(
            nameof(IntrinsicNodeWidth),
            control => control.IntrinsicNodeWidth);

    /// <summary>
    /// Creates the shared execution-graph node control base and hooks the common view-model lifetime events.
    /// </summary>
    protected ExecutionGraphNodeControlBase()
    {
        DataContextChanged += HandleDataContextChanged;
        AttachedToVisualTree += HandleAttachedToVisualTree;
        DetachedFromVisualTree += HandleDetachedFromVisualTree;
    }

    /// <summary>
    /// Raised when the node is clicked with the primary pointer button.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? Invoked;

    /// <summary>
    /// Gets the current frame thickness for the node chrome.
    /// </summary>
    public Thickness FrameBorderThickness => _observedNode?.IsSelected == true
        ? SelectedBorderThickness
        : DefaultBorderThickness;

    /// <summary>
    /// Gets the node's current intrinsic width resolved from its real visible content.
    /// </summary>
    public double IntrinsicNodeWidth
    {
        get => _intrinsicNodeWidth;
        private set => SetAndRaise(IntrinsicNodeWidthProperty, ref _intrinsicNodeWidth, value);
    }

    /// <summary>
    /// Gets the currently observed graph node view model when one is bound.
    /// </summary>
    protected ExecutionNodeViewModel? ObservedNode => _observedNode;

    /// <summary>
    /// Gets the current task-view-model status for derived semantic styling logic.
    /// </summary>
    protected bool IsHovered => _isHovered;

    /// <summary>
    /// Gets the current pressed-state flag for derived semantic styling logic.
    /// </summary>
    protected bool IsPressed => _isPressed;

    /// <summary>
    /// Finishes common initialization after the derived class has loaded its XAML and can provide the named click surface
    /// plus intrinsic content host from the real visible control tree.
    /// </summary>
    protected void InitializeNodeControl(string clickSurfaceName, string intrinsicHostName)
    {
        _clickSurface = GetRequiredControl<Border>(clickSurfaceName);
        _intrinsicSizeHost = GetRequiredControl<IntrinsicSizeHost>(intrinsicHostName);

        _clickSurface.PointerEntered += ClickSurface_PointerEntered;
        _clickSurface.PointerExited += ClickSurface_PointerExited;
        _clickSurface.PointerPressed += ClickSurface_PointerPressed;
        _clickSurface.PointerReleased += ClickSurface_PointerReleased;
        ((INotifyPropertyChanged)_intrinsicSizeHost).PropertyChanged += HandleIntrinsicHostPropertyChanged;
    }

    /// <summary>
    /// Looks up one named control from the compiled XAML so derived controls can cache strongly typed visual references.
    /// </summary>
    protected T GetRequiredControl<T>(string name) where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"{GetType().Name} control '{name}' was not initialized.");
    }

    /// <summary>
    /// Rebinds property-change observation when the control starts rendering a different node view model.
    /// </summary>
    private void HandleDataContextChanged(object? sender, EventArgs e)
    {
        Thickness previousThickness = FrameBorderThickness;
        DetachObservedNodeSubscriptions();
        _observedNode = DataContext as ExecutionNodeViewModel;
        AttachObservedNodeSubscriptions();

        ApplySemanticClasses();
        UpdateIntrinsicNodeWidthIfNeeded();
        RaisePropertyChanged(FrameBorderThicknessProperty, previousThickness, FrameBorderThickness);
    }

    /// <summary>
    /// Restores view-model subscriptions when a retained node control re-enters the visual tree with the same data context.
    /// </summary>
    private void HandleAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachObservedNodeSubscriptions();
    }

    /// <summary>
    /// Releases task and node subscriptions when the control leaves the visual tree so removed retained controls and any
    /// other transient visual-tree instances do not stay alive through shared task-view-model event handlers.
    /// </summary>
    private void HandleDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachObservedNodeSubscriptions();
    }

    /// <summary>
    /// Applies the hover class while the pointer is over the node hit surface.
    /// </summary>
    private void ClickSurface_PointerEntered(object? sender, PointerEventArgs e)
    {
        _isHovered = true;
        ApplySemanticClasses();
    }

    /// <summary>
    /// Clears transient interaction classes once the pointer leaves the node hit surface.
    /// </summary>
    private void ClickSurface_PointerExited(object? sender, PointerEventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        ApplySemanticClasses();
    }

    /// <summary>
    /// Marks the node as pressed on primary-button down so the graph keeps immediate pointer feedback without generic
    /// button chrome.
    /// </summary>
    private void ClickSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_clickSurface!).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPressed = true;
        ApplySemanticClasses();
        e.Handled = true;
    }

    /// <summary>
    /// Raises the custom invoked event when the primary-button release lands on the node hit surface.
    /// </summary>
    private void ClickSurface_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool isInsideNode = _clickSurface!.Bounds.Contains(e.GetPosition(_clickSurface));
        _isPressed = false;
        ApplySemanticClasses();
        if (!isInsideNode || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        Invoked?.Invoke(this, new RoutedEventArgs());
        e.Handled = true;
    }

    /// <summary>
    /// Reapplies selection-owned visuals whenever the graph node selection state changes.
    /// </summary>
    private void HandleObservedNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        ApplySemanticClasses();
        Thickness nextThickness = FrameBorderThickness;
        Thickness previousThickness = nextThickness.Equals(SelectedBorderThickness)
            ? DefaultBorderThickness
            : SelectedBorderThickness;
        RaisePropertyChanged(FrameBorderThicknessProperty, previousThickness, nextThickness);
    }

    /// <summary>
    /// Delegates task-owned visual changes to the concrete control so each node kind can react only to the task state it
    /// actually renders in its XAML.
    /// </summary>
    private void HandleObservedTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        HandleObservedTaskPropertyChangedCore(e.PropertyName);
    }

    /// <summary>
    /// Recomputes the intrinsic node width whenever the real visible content host reports a new unconstrained desired width.
    /// </summary>
    private void HandleIntrinsicHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(IntrinsicSizeHost.IntrinsicWidth), StringComparison.Ordinal))
        {
            return;
        }

        UpdateIntrinsicNodeWidthIfNeeded();
    }

    /// <summary>
    /// Stores the latest intrinsic node width derived from the concrete control's real visible content.
    /// </summary>
    private void UpdateIntrinsicNodeWidthIfNeeded()
    {
        if (_intrinsicSizeHost == null)
        {
            return;
        }

        IntrinsicNodeWidth = ComputeIntrinsicNodeWidth(_intrinsicSizeHost.IntrinsicWidth);
    }

    /// <summary>
    /// Hooks the current node and task view models exactly once while the control is alive in the visual tree.
    /// </summary>
    private void AttachObservedNodeSubscriptions()
    {
        if (_subscriptionsAttached || _observedNode == null)
        {
            return;
        }

        _observedNode.PropertyChanged += HandleObservedNodePropertyChanged;
        _observedNode.Task.PropertyChanged += HandleObservedTaskPropertyChanged;
        _subscriptionsAttached = true;
    }

    /// <summary>
    /// Unhooks the current node and task view models when the control is detached or rebound.
    /// </summary>
    private void DetachObservedNodeSubscriptions()
    {
        if (!_subscriptionsAttached || _observedNode == null)
        {
            return;
        }

        _observedNode.PropertyChanged -= HandleObservedNodePropertyChanged;
        _observedNode.Task.PropertyChanged -= HandleObservedTaskPropertyChanged;
        _subscriptionsAttached = false;
    }

    /// <summary>
    /// Lets the concrete control apply its semantic status and interaction classes to its own cached visual elements.
    /// </summary>
    protected abstract void ApplySemanticClassesCore(ExecutionNodeViewModel observedNode, bool isHovered, bool isPressed);

    /// <summary>
    /// Lets the concrete control respond only to the task-view-model properties that affect its own visuals.
    /// </summary>
    protected abstract void HandleObservedTaskPropertyChangedCore(string? propertyName);

    /// <summary>
    /// Converts the hosted intrinsic content width into the final intrinsic node width consumed by graph layout.
    /// </summary>
    protected abstract double ComputeIntrinsicNodeWidth(double intrinsicContentWidth);

    /// <summary>
    /// Applies the node semantic classes consumed by the local Avalonia styles when a node is currently bound.
    /// </summary>
    protected void ApplySemanticClasses()
    {
        if (_observedNode == null)
        {
            return;
        }

        ApplySemanticClassesCore(_observedNode, _isHovered, _isPressed);
    }
}
