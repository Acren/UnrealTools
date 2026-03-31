using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders the shared TIME/WARN/ERR metric pills used by the workspace header and execution-graph nodes.
/// </summary>
public partial class ExecutionMetricsStrip : UserControl
{
    /// <summary>
    /// Identifies the formatted time value shown in the TIME pill.
    /// </summary>
    public static readonly StyledProperty<string> TimeTextProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, string>(nameof(TimeText), "--:--");

    /// <summary>
    /// Identifies the warning count shown in the WARN pill.
    /// </summary>
    public static readonly StyledProperty<int> WarningCountProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, int>(nameof(WarningCount));

    /// <summary>
    /// Identifies the error count shown in the ERR pill.
    /// </summary>
    public static readonly StyledProperty<int> ErrorCountProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, int>(nameof(ErrorCount));

    /// <summary>
    /// Identifies whether the warning pill should use the warning accent.
    /// </summary>
    public static readonly StyledProperty<bool> HasWarningsProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, bool>(nameof(HasWarnings));

    /// <summary>
    /// Identifies whether the error pill should use the error accent.
    /// </summary>
    public static readonly StyledProperty<bool> HasErrorsProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, bool>(nameof(HasErrors));

    /// <summary>
    /// Creates the shared execution metrics strip.
    /// </summary>
    public ExecutionMetricsStrip()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the formatted time value shown in the TIME pill.
    /// </summary>
    public string TimeText
    {
        get => GetValue(TimeTextProperty);
        set => SetValue(TimeTextProperty, value);
    }

    /// <summary>
    /// Gets or sets the warning count shown in the WARN pill.
    /// </summary>
    public int WarningCount
    {
        get => GetValue(WarningCountProperty);
        set => SetValue(WarningCountProperty, value);
    }

    /// <summary>
    /// Gets or sets the error count shown in the ERR pill.
    /// </summary>
    public int ErrorCount
    {
        get => GetValue(ErrorCountProperty);
        set => SetValue(ErrorCountProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the warning pill should use the warning accent.
    /// </summary>
    public bool HasWarnings
    {
        get => GetValue(HasWarningsProperty);
        set => SetValue(HasWarningsProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the error pill should use the error accent.
    /// </summary>
    public bool HasErrors
    {
        get => GetValue(HasErrorsProperty);
        set => SetValue(HasErrorsProperty, value);
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the metrics strip.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
