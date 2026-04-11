using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders one execution-graph group container so canvas code only needs to manage positioning and graph interactions.
/// </summary>
public partial class ExecutionGroupContainer : ExecutionGraphNodeControlBase
{
    private static readonly double StableFrameHorizontalInset = 4.0;
    private Border? _clickSurface;
    private Border? _backgroundShell;
    private Border? _borderChrome;
    private Border? _headerShell;

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
    /// Creates the XAML-backed execution-graph group container control.
    /// </summary>
    public ExecutionGroupContainer()
    {
        InitializeComponent();
        _clickSurface = GetRequiredControl<Border>("ClickSurface");
        _backgroundShell = GetRequiredControl<Border>("ContainerBackgroundShell");
        _borderChrome = GetRequiredControl<Border>("ContainerBorderChrome");
        _headerShell = GetRequiredControl<Border>("HeaderShell");
        InitializeNodeControl(clickSurfaceName: "ClickSurface", intrinsicHostName: "HeaderIntrinsicHost");
    }

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
    /// Loads the compiled Avalonia markup for the group container.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Reapplies status-owned visuals whenever the nested task view model changes in a way that alters semantic chrome.
    /// </summary>
    protected override void HandleObservedTaskPropertyChangedCore(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName) ||
            string.Equals(propertyName, nameof(ExecutionTaskViewModel.DisplayStatus), StringComparison.Ordinal))
        {
            ApplySemanticClasses();
        }
    }

    /// <summary>
    /// Applies the group semantic classes consumed by the local Avalonia styles.
    /// </summary>
    protected override void ApplySemanticClassesCore(ExecutionNodeViewModel observedNode, bool isHovered, bool isPressed)
    {
        /* Group background layers need the same semantic classes as the frame so disabled groups read as dimmer
           containers across both the body and header surfaces. */
        ExecutionStatusClasses.ApplyInteractionClasses(_backgroundShell!.Classes, observedNode.IsSelected, isHovered, isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(_backgroundShell.Classes, observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyInteractionClasses(_borderChrome!.Classes, observedNode.IsSelected, isHovered, isPressed);
        ExecutionStatusClasses.ApplyInteractionClasses(_headerShell!.Classes, observedNode.IsSelected, isHovered, isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(_headerShell.Classes, observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyStatusClasses(_borderChrome.Classes, observedNode.Task.DisplayStatus);
    }

    /// <summary>
    /// Converts the hosted intrinsic header width into the final intrinsic group width consumed by graph layout.
    /// </summary>
    protected override double ComputeIntrinsicNodeWidth(double intrinsicContentWidth)
    {
        return Math.Max(ExecutionGraphLayoutSettings.NodeMinWidth, intrinsicContentWidth + StableFrameHorizontalInset);
    }
}
