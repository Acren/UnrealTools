using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Avalonia.ViewModels;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders one execution-graph group container so canvas code only needs to manage positioning and graph interactions.
/// </summary>
public partial class ExecutionGroupContainer : UserControl
{
    private static readonly Thickness SelectedBorderThickness = new(2.0);
    private static readonly Thickness DefaultBorderThickness = new(1.5);
    private static readonly double StableFrameHorizontalInset = Math.Max(DefaultBorderThickness.Left, SelectedBorderThickness.Left) * 2.0;
    private bool _isHovered;
    private bool _isPressed;
    private bool _subscriptionsAttached;
    private ExecutionNodeViewModel? _observedNode;
    private double _intrinsicNodeWidth = ExecutionGraphLayoutSettings.NodeMinWidth;
    private Border? _clickSurface;
    private Border? _backgroundShell;
    private Border? _borderChrome;
    private Border? _headerShell;
    private IntrinsicSizeHost? _headerIntrinsicHost;

    /// <summary>
    /// Identifies the rendered width for the group container. Defaults to NaN so the control can still render naturally
    /// in isolation, while graph layout normally supplies the explicit outer frame width.
    /// </summary>
    public static readonly StyledProperty<double> GroupWidthProperty =
        AvaloniaProperty.Register<ExecutionGroupContainer, double>(nameof(GroupWidth), double.NaN);

    /// <summary>
    /// Identifies the rendered height for the group container.
    /// </summary>
    public static readonly StyledProperty<double> GroupHeightProperty =
        AvaloniaProperty.Register<ExecutionGroupContainer, double>(nameof(GroupHeight));

    /// <summary>
    /// Identifies the rendered header height used inside the framed group container.
    /// </summary>
    public static readonly StyledProperty<double> HeaderHeightProperty =
        AvaloniaProperty.Register<ExecutionGroupContainer, double>(nameof(HeaderHeight));

    /// <summary>
    /// Identifies the current frame thickness used by the group border and header inset.
    /// </summary>
    public static readonly DirectProperty<ExecutionGroupContainer, Thickness> FrameBorderThicknessProperty =
        AvaloniaProperty.RegisterDirect<ExecutionGroupContainer, Thickness>(
            nameof(FrameBorderThickness),
            control => control.FrameBorderThickness);

    /// <summary>
    /// Identifies the group's current intrinsic width resolved from its real visible header content.
    /// </summary>
    public static readonly DirectProperty<ExecutionGroupContainer, double> IntrinsicNodeWidthProperty =
        AvaloniaProperty.RegisterDirect<ExecutionGroupContainer, double>(
            nameof(IntrinsicNodeWidth),
            control => control.IntrinsicNodeWidth);

    /// <summary>
    /// Creates the XAML-backed execution-graph group container control.
    /// </summary>
    public ExecutionGroupContainer()
    {
        InitializeComponent();
        _clickSurface!.PointerEntered += ClickSurface_PointerEntered;
        _clickSurface.PointerExited += ClickSurface_PointerExited;
        _clickSurface.PointerPressed += ClickSurface_PointerPressed;
        _clickSurface.PointerReleased += ClickSurface_PointerReleased;
        DataContextChanged += HandleDataContextChanged;
        AttachedToVisualTree += HandleAttachedToVisualTree;
        DetachedFromVisualTree += HandleDetachedFromVisualTree;
    }

    /// <summary>
    /// Raised when the group header is clicked with the pointer.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? Invoked;

    /// <summary>
    /// Gets or sets the rendered width for the group container.
    /// </summary>
    public double GroupWidth
    {
        get => GetValue(GroupWidthProperty);
        set => SetValue(GroupWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the rendered height for the group container.
    /// </summary>
    public double GroupHeight
    {
        get => GetValue(GroupHeightProperty);
        set => SetValue(GroupHeightProperty, value);
    }

    /// <summary>
    /// Gets or sets the height reserved for the group header.
    /// </summary>
    public double HeaderHeight
    {
        get => GetValue(HeaderHeightProperty);
        set => SetValue(HeaderHeightProperty, value);
    }

    /// <summary>
    /// Gets the current frame thickness for the group border and header inset.
    /// </summary>
    public Thickness FrameBorderThickness => _observedNode?.IsSelected == true
        ? SelectedBorderThickness
        : DefaultBorderThickness;

    /// <summary>
    /// Gets the group's current intrinsic width resolved from its real visible header content.
    /// </summary>
    public double IntrinsicNodeWidth
    {
        get => _intrinsicNodeWidth;
        private set => SetAndRaise(IntrinsicNodeWidthProperty, ref _intrinsicNodeWidth, value);
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the group container.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _clickSurface = GetRequiredControl<Border>("ClickSurface");
        _backgroundShell = GetRequiredControl<Border>("ContainerBackgroundShell");
        _borderChrome = GetRequiredControl<Border>("ContainerBorderChrome");
        _headerShell = GetRequiredControl<Border>("HeaderShell");
        _headerIntrinsicHost = GetRequiredControl<IntrinsicSizeHost>("HeaderIntrinsicHost");
        ((INotifyPropertyChanged)_headerIntrinsicHost).PropertyChanged += HandleHeaderIntrinsicHostPropertyChanged;
    }

    /// <summary>
    /// Applies the hover class while the pointer is over the group header hit surface.
    /// </summary>
    private void ClickSurface_PointerEntered(object? sender, PointerEventArgs e)
    {
        _isHovered = true;
        ApplySemanticClasses();
    }

    /// <summary>
    /// Clears transient interaction classes once the pointer leaves the group header.
    /// </summary>
    private void ClickSurface_PointerExited(object? sender, PointerEventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        ApplySemanticClasses();
    }

    /// <summary>
    /// Marks the header as pressed on primary-button down so the graph keeps pointer feedback without generic button chrome.
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
    /// Raises the custom invoked event when the primary-button release lands on the group header hit surface.
    /// </summary>
    private void ClickSurface_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool isInsideHeader = _clickSurface!.Bounds.Contains(e.GetPosition(_clickSurface));
        _isPressed = false;
        ApplySemanticClasses();
        if (!isInsideHeader || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        Invoked?.Invoke(this, new RoutedEventArgs());
        e.Handled = true;
    }

    /// <summary>
    /// Looks up one named control from the compiled XAML and caches a typed reference so later updates avoid repeated
    /// string-based tree lookups.
    /// </summary>
    private T GetRequiredControl<T>(string name) where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"ExecutionGroupContainer control '{name}' was not initialized.");
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
    /// Restores view-model subscriptions when a retained group control re-enters the visual tree with the same data context.
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
    /// Reapplies status-owned visuals whenever the nested task view model changes in a way that alters semantic chrome.
    /// </summary>
    private void HandleObservedTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.DisplayStatus), StringComparison.Ordinal))
        {
            ApplySemanticClasses();
        }
    }

    /// <summary>
    /// Applies the group semantic classes consumed by the local Avalonia styles.
    /// </summary>
    private void ApplySemanticClasses()
    {
        if (_observedNode == null)
        {
            return;
        }

        /* Group background layers need the same semantic classes as the frame so disabled groups read as dimmer
           containers across both the body and header surfaces. */
        ExecutionStatusClasses.ApplyInteractionClasses(_backgroundShell!.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(_backgroundShell.Classes, _observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyInteractionClasses(_borderChrome!.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyInteractionClasses(_headerShell!.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(_headerShell.Classes, _observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyStatusClasses(_borderChrome.Classes, _observedNode.Task.DisplayStatus);
    }

    /// <summary>
    /// Recomputes the intrinsic width whenever the real visible header host reports a new unconstrained desired width.
    /// </summary>
    private void HandleHeaderIntrinsicHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(IntrinsicSizeHost.IntrinsicWidth), StringComparison.Ordinal))
        {
            return;
        }

        UpdateIntrinsicNodeWidthIfNeeded();
    }

    /// <summary>
    /// Stores the latest intrinsic group width derived from the real visible header row plus the stable frame inset.
    /// </summary>
    private void UpdateIntrinsicNodeWidthIfNeeded()
    {
        if (_headerIntrinsicHost == null)
        {
            return;
        }

        IntrinsicNodeWidth = Math.Max(
            ExecutionGraphLayoutSettings.NodeMinWidth,
            _headerIntrinsicHost.IntrinsicWidth + StableFrameHorizontalInset);
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

}
