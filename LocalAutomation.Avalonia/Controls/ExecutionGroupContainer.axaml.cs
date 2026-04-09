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
    private const double WidthChangeThreshold = 0.5;
    private bool _isHovered;
    private bool _isPressed;
    private ExecutionNodeViewModel? _observedNode;
    private double _measuredHeaderMinWidth = ExecutionGraphLayoutSettings.NodeMinWidth;
    private Border? _clickSurface;
    private Border? _backgroundShell;
    private Border? _borderChrome;
    private Border? _headerShell;
    private MeasuredSizeHost? _headerMeasureHost;

    /// <summary>
    /// Identifies the rendered width for the group container. Defaults to NaN so detached measurement can use the
    /// control's natural XAML width while final graph rendering can still impose the outer frame width from layout.
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
    /// Identifies the minimum header width resolved from the visible control's own layout.
    /// </summary>
    public static readonly DirectProperty<ExecutionGroupContainer, double> MeasuredHeaderMinWidthProperty =
        AvaloniaProperty.RegisterDirect<ExecutionGroupContainer, double>(
            nameof(MeasuredHeaderMinWidth),
            control => control.MeasuredHeaderMinWidth);

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
    /// Gets the minimum width required by the current header content.
    /// </summary>
    public double MeasuredHeaderMinWidth
    {
        get => _measuredHeaderMinWidth;
        private set => SetAndRaise(MeasuredHeaderMinWidthProperty, ref _measuredHeaderMinWidth, value);
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the group container.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        SizeChanged += HandleSizeChanged;
        _clickSurface = GetRequiredControl<Border>("ClickSurface");
        _backgroundShell = GetRequiredControl<Border>("ContainerBackgroundShell");
        _borderChrome = GetRequiredControl<Border>("ContainerBorderChrome");
        _headerShell = GetRequiredControl<Border>("HeaderShell");
        _headerMeasureHost = GetRequiredControl<MeasuredSizeHost>("HeaderMeasureHost");
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
        if (_observedNode != null)
        {
            _observedNode.PropertyChanged -= HandleObservedNodePropertyChanged;
        }

        _observedNode = DataContext as ExecutionNodeViewModel;
        if (_observedNode != null)
        {
            _observedNode.PropertyChanged += HandleObservedNodePropertyChanged;
        }

        ApplySemanticClasses();
        MeasuredHeaderMinWidth = ExecutionGraphLayoutSettings.NodeMinWidth;
        UpdateMeasuredHeaderMinWidthIfNeeded();
    }

    /// <summary>
    /// Reapplies semantic classes whenever the node's status or selection state changes.
    /// </summary>
    private void HandleObservedNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.Status), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.DisplayStatus), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.IsSelected), StringComparison.Ordinal))
        {
            ApplySemanticClasses();
        }
    }

    /// <summary>
    /// Re-reports the header minimum width when Avalonia resolves a different on-screen size for the group container.
    /// </summary>
    private void HandleSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateMeasuredHeaderMinWidthIfNeeded();
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
        ExecutionStatusClasses.ApplyStatusClasses(_backgroundShell.Classes, _observedNode.DisplayStatus);
        ExecutionStatusClasses.ApplyInteractionClasses(_borderChrome!.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyInteractionClasses(_headerShell!.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(_headerShell.Classes, _observedNode.DisplayStatus);
        ExecutionStatusClasses.ApplyStatusClasses(_borderChrome.Classes, _observedNode.DisplayStatus);
    }

    /// <summary>
    /// Measures the live header content and updates the control-owned minimum width when it changes materially.
    /// </summary>
    private void UpdateMeasuredHeaderMinWidthIfNeeded()
    {
        if (_observedNode == null || !_observedNode.IsContainer)
        {
            return;
        }

        if (_headerMeasureHost == null || _clickSurface == null)
        {
            return;
        }

        double measuredWidth = Math.Max(
            ExecutionGraphLayoutSettings.NodeMinWidth,
            _headerMeasureHost.MeasuredWidth + _clickSurface.Margin.Left + _clickSurface.Margin.Right);
        if (Math.Abs(MeasuredHeaderMinWidth - measuredWidth) <= WidthChangeThreshold)
        {
            return;
        }

        MeasuredHeaderMinWidth = measuredWidth;
    }
}
