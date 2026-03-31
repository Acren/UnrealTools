using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Layout;
using System.ComponentModel;
using LocalAutomation.Core;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders one execution-graph group container so canvas code only needs to manage positioning and graph interactions.
/// </summary>
public partial class ExecutionGroupContainer : UserControl
{
    private bool _isHovered;
    private bool _isPressed;
    private ExecutionNodeViewModel? _observedNode;

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
    /// Creates the XAML-backed execution-graph group container control.
    /// </summary>
    public ExecutionGroupContainer()
    {
        InitializeComponent();
        Border clickSurface = GetRequiredBorder("ClickSurface");
        clickSurface.PointerEntered += ClickSurface_PointerEntered;
        clickSurface.PointerExited += ClickSurface_PointerExited;
        clickSurface.PointerPressed += ClickSurface_PointerPressed;
        clickSurface.PointerReleased += ClickSurface_PointerReleased;
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
    /// Returns the actual rendered header-content width, including the header grid margin, so graph layout can size the
    /// outer group frame from the same XAML measurement that the user sees on screen.
    /// </summary>
    public double GetHeaderContentWidth()
    {
        Grid headerContentGrid = this.FindControl<Grid>("HeaderContentGrid")
            ?? throw new InvalidOperationException("ExecutionGroupContainer header content grid was not initialized.");
        Thickness margin = headerContentGrid.Margin;
        headerContentGrid.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double contentWidth = headerContentGrid.DesiredSize.Width + margin.Left + margin.Right;
        return Math.Max(1, contentWidth);
    }

    /// <summary>
      /// Loads the compiled Avalonia markup for the group container.
      /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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
        Border clickSurface = GetRequiredBorder("ClickSurface");
        if (!e.GetCurrentPoint(clickSurface).Properties.IsLeftButtonPressed)
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
        Border clickSurface = GetRequiredBorder("ClickSurface");
        bool isInsideHeader = clickSurface.Bounds.Contains(e.GetPosition(clickSurface));
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
    /// Looks up one named border from the compiled XAML so pointer handlers can manipulate the visual shell directly.
    /// </summary>
    private Border GetRequiredBorder(string name)
    {
        return this.FindControl<Border>(name)
            ?? throw new InvalidOperationException($"ExecutionGroupContainer border '{name}' was not initialized.");
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
    }

    /// <summary>
    /// Reapplies semantic classes whenever the node's status or selection state changes.
    /// </summary>
    private void HandleObservedNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.Status), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.IsSelected), StringComparison.Ordinal))
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

        Border backgroundShell = GetRequiredBorder("ContainerBackgroundShell");
        Border borderChrome = GetRequiredBorder("ContainerBorderChrome");
        Border headerShell = GetRequiredBorder("HeaderShell");
        ExecutionStatusClasses.ApplyInteractionClasses(backgroundShell.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyInteractionClasses(borderChrome.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyInteractionClasses(headerShell.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(borderChrome.Classes, _observedNode.Status);
    }
}
