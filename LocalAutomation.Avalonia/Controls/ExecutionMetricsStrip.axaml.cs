using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders the shared TIME/WARN/ERR metric pills used by the workspace header and execution-graph nodes.
/// </summary>
public partial class ExecutionMetricsStrip : UserControl
{
    /// <summary>
    /// Identifies the raw execution metrics bound into the shared strip.
    /// </summary>
    public static readonly StyledProperty<ExecutionTaskMetrics> MetricsProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, ExecutionTaskMetrics>(nameof(Metrics));

    /// <summary>
    /// Identifies the raw duration derived from the metrics.
    /// </summary>
    public static readonly StyledProperty<TimeSpan?> DurationProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, TimeSpan?>(nameof(Duration));

    /// <summary>
    /// Identifies the warning count derived from the raw metrics.
    /// </summary>
    public static readonly StyledProperty<int> WarningCountProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, int>(nameof(WarningCount));

    /// <summary>
    /// Identifies the error count derived from the raw metrics.
    /// </summary>
    public static readonly StyledProperty<int> ErrorCountProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, int>(nameof(ErrorCount));

    /// <summary>
    /// Identifies whether the warning accent should be enabled for the current metrics.
    /// </summary>
    public static readonly StyledProperty<bool> HasWarningsProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, bool>(nameof(HasWarnings));

    /// <summary>
    /// Identifies whether the error accent should be enabled for the current metrics.
    /// </summary>
    public static readonly StyledProperty<bool> HasErrorsProperty =
        AvaloniaProperty.Register<ExecutionMetricsStrip, bool>(nameof(HasErrors));

    /// <summary>
    /// Creates the shared execution metrics strip.
    /// </summary>
    public ExecutionMetricsStrip()
    {
        InitializeComponent();
        ApplyMetrics(Metrics);
    }

    /// <summary>
    /// Gets or sets the raw execution metrics displayed by the strip.
    /// </summary>
    public ExecutionTaskMetrics Metrics
    {
        get => GetValue(MetricsProperty);
        set => SetValue(MetricsProperty, value);
    }

    /// <summary>
    /// Gets the raw duration shown by the TIME pill.
    /// </summary>
    public TimeSpan? Duration
    {
        get => GetValue(DurationProperty);
        private set => SetValue(DurationProperty, value);
    }

    /// <summary>
    /// Gets the warning count shown in the WARN pill.
    /// </summary>
    public int WarningCount
    {
        get => GetValue(WarningCountProperty);
        private set => SetValue(WarningCountProperty, value);
    }

    /// <summary>
    /// Gets the error count shown in the ERR pill.
    /// </summary>
    public int ErrorCount
    {
        get => GetValue(ErrorCountProperty);
        private set => SetValue(ErrorCountProperty, value);
    }

    /// <summary>
    /// Gets whether the warning pill should use the warning accent.
    /// </summary>
    public bool HasWarnings
    {
        get => GetValue(HasWarningsProperty);
        private set => SetValue(HasWarningsProperty, value);
    }

    /// <summary>
    /// Gets whether the error pill should use the error accent.
    /// </summary>
    public bool HasErrors
    {
        get => GetValue(HasErrorsProperty);
        private set => SetValue(HasErrorsProperty, value);
    }

    /// <summary>
    /// Projects raw metrics into the strip's internal display properties whenever the single public Metrics input changes.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MetricsProperty && change.NewValue is ExecutionTaskMetrics metrics)
        {
            ApplyMetrics(metrics);
        }
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the metrics strip.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Derives all display-facing pill values from one raw metrics object so callers cannot pass inconsistent booleans.
    /// </summary>
    private void ApplyMetrics(ExecutionTaskMetrics metrics)
    {
        Duration = metrics.Duration;
        WarningCount = metrics.WarningCount;
        ErrorCount = metrics.ErrorCount;
        HasWarnings = metrics.WarningCount > 0;
        HasErrors = metrics.ErrorCount > 0;
    }
}
