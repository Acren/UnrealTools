using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders one execution-graph group container so canvas code only needs to manage positioning and graph interactions.
/// </summary>
public partial class ExecutionGroupContainer : UserControl
{
    /// <summary>
    /// Identifies the rendered width for the group container.
    /// </summary>
    public static readonly StyledProperty<double> GroupWidthProperty =
        AvaloniaProperty.Register<ExecutionGroupContainer, double>(nameof(GroupWidth));

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
    /// Returns the named click surface so the graph canvas can attach centralized selection behavior.
    /// </summary>
    public Button GetClickSurface()
    {
        return this.FindControl<Button>("ClickSurface")
            ?? throw new InvalidOperationException("ExecutionGroupContainer click surface was not initialized.");
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the group container.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
