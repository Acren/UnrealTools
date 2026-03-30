using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders one execution-graph task card so graph canvas code only needs to place it and connect graph interactions.
/// </summary>
public partial class ExecutionTaskCard : UserControl
{
    /// <summary>
    /// Identifies the rendered width for the task card.
    /// </summary>
    public static readonly StyledProperty<double> CardWidthProperty =
        AvaloniaProperty.Register<ExecutionTaskCard, double>(nameof(CardWidth));

    /// <summary>
    /// Identifies the rendered height for the task card.
    /// </summary>
    public static readonly StyledProperty<double> CardHeightProperty =
        AvaloniaProperty.Register<ExecutionTaskCard, double>(nameof(CardHeight));

    /// <summary>
    /// Creates the XAML-backed execution-graph task card control.
    /// </summary>
    public ExecutionTaskCard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the rendered width for the task card.
    /// </summary>
    public double CardWidth
    {
        get => GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    /// <summary>
    /// Gets or sets the rendered height for the task card.
    /// </summary>
    public double CardHeight
    {
        get => GetValue(CardHeightProperty);
        set => SetValue(CardHeightProperty, value);
    }

    /// <summary>
    /// Returns the named click surface so the graph canvas can attach centralized selection behavior.
    /// </summary>
    public Button GetClickSurface()
    {
        return this.FindControl<Button>("ClickSurface")
            ?? throw new InvalidOperationException("ExecutionTaskCard click surface was not initialized.");
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the task card.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
