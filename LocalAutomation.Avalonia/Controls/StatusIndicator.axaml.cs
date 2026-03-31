using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders the shared status language used by runtime tabs and execution-graph nodes so the workspace presents one
/// consistent set of status signals while allowing each host to choose whether the label is shown.
/// </summary>
public partial class StatusIndicator : UserControl
{
    private Border? _dotBorder;
    private TextBlock? _labelTextBlock;

    /// <summary>
    /// Identifies the semantic status value rendered by the indicator.
    /// </summary>
    public static readonly StyledProperty<global::LocalAutomation.Core.ExecutionTaskStatus> StatusProperty =
        AvaloniaProperty.Register<StatusIndicator, global::LocalAutomation.Core.ExecutionTaskStatus>(nameof(Status), global::LocalAutomation.Core.ExecutionTaskStatus.Pending);

    /// <summary>
    /// Identifies whether the text label should be shown next to the dot.
    /// </summary>
    public static readonly StyledProperty<bool> ShowLabelProperty =
        AvaloniaProperty.Register<StatusIndicator, bool>(nameof(ShowLabel), true);

    /// <summary>
    /// Identifies the rendered dot size.
    /// </summary>
    public static readonly StyledProperty<double> DotSizeProperty =
        AvaloniaProperty.Register<StatusIndicator, double>(nameof(DotSize), 6);

    /// <summary>
    /// Identifies the label font size.
    /// </summary>
    public static readonly StyledProperty<double> LabelFontSizeProperty =
        AvaloniaProperty.Register<StatusIndicator, double>(nameof(LabelFontSize), 11);

    /// <summary>
    /// Creates the shared status-indicator control.
    /// </summary>
    public StatusIndicator()
    {
        InitializeComponent();
        ApplySemanticState();
    }

    /// <summary>
    /// Gets or sets the semantic status rendered by the control.
    /// </summary>
    public global::LocalAutomation.Core.ExecutionTaskStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the label should be shown.
    /// </summary>
    public bool ShowLabel
    {
        get => GetValue(ShowLabelProperty);
        set => SetValue(ShowLabelProperty, value);
    }

    /// <summary>
    /// Gets or sets the rendered dot size.
    /// </summary>
    public double DotSize
    {
        get => GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    /// <summary>
    /// Gets or sets the label font size.
    /// </summary>
    public double LabelFontSize
    {
        get => GetValue(LabelFontSizeProperty);
        set => SetValue(LabelFontSizeProperty, value);
    }

    /// <summary>
    /// Reapplies the semantic status classes and label text whenever one of the public state properties changes.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == StatusProperty ||
            change.Property == ShowLabelProperty ||
            change.Property == DotSizeProperty ||
            change.Property == LabelFontSizeProperty)
        {
            ApplySemanticState();
        }
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the control.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _dotBorder = this.FindControl<Border>("DotBorder");
        _labelTextBlock = this.FindControl<TextBlock>("LabelTextBlock");
    }

    /// <summary>
    /// Updates the dot classes and label text from the current semantic status while leaving arrangement and animation
    /// fully declarative in XAML styles.
    /// </summary>
    private void ApplySemanticState()
    {
        if (_dotBorder == null || _labelTextBlock == null)
        {
            return;
        }

        _dotBorder.Width = DotSize;
        _dotBorder.Height = DotSize;

        _labelTextBlock.IsVisible = ShowLabel;
        _labelTextBlock.Text = GetLabelText(Status);
        _labelTextBlock.FontSize = LabelFontSize;

        ApplyStatusClasses(_dotBorder.Classes, Status);
        ApplyStatusClasses(_labelTextBlock.Classes, Status);
    }

    /// <summary>
    /// Maps one semantic status to the shared short label used across tabs and graph nodes.
    /// </summary>
    private static string GetLabelText(global::LocalAutomation.Core.ExecutionTaskStatus status)
    {
        return status switch
        {
            global::LocalAutomation.Core.ExecutionTaskStatus.Completed => "Done",
            global::LocalAutomation.Core.ExecutionTaskStatus.Pending => "Pending",
            global::LocalAutomation.Core.ExecutionTaskStatus.Blocked => "Blocked",
            global::LocalAutomation.Core.ExecutionTaskStatus.Running => "Running",
            global::LocalAutomation.Core.ExecutionTaskStatus.Failed => "Failed",
            global::LocalAutomation.Core.ExecutionTaskStatus.Skipped => "Skipped",
            global::LocalAutomation.Core.ExecutionTaskStatus.Disabled => "Disabled",
            global::LocalAutomation.Core.ExecutionTaskStatus.Cancelled => "Cancelled",
            global::LocalAutomation.Core.ExecutionTaskStatus.Planned => "Planned",
            _ => status.ToString()
        };
    }

    /// <summary>
    /// Applies the shared status classes consumed by the XAML styles. Pending, planned, skipped, and disabled all fall
    /// back to the muted base style, while active and terminal states add one accent class.
    /// </summary>
    private static void ApplyStatusClasses(Classes classes, global::LocalAutomation.Core.ExecutionTaskStatus status)
    {
        classes.Set("running", status == global::LocalAutomation.Core.ExecutionTaskStatus.Running);
        classes.Set("succeeded", status == global::LocalAutomation.Core.ExecutionTaskStatus.Completed);
        classes.Set("failed", status == global::LocalAutomation.Core.ExecutionTaskStatus.Failed);
        classes.Set("blocked", status == global::LocalAutomation.Core.ExecutionTaskStatus.Blocked);
        classes.Set("cancelled", status == global::LocalAutomation.Core.ExecutionTaskStatus.Cancelled);
    }
}
