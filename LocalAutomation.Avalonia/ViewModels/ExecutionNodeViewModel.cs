using System;
using Avalonia;
using Avalonia.Media;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Core;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;
using RuntimeExecutionTaskMetrics = LocalAutomation.Runtime.ExecutionTaskMetrics;
using RuntimeExecutionTaskOutcome = LocalAutomation.Runtime.ExecutionTaskOutcome;
using RuntimeExecutionTaskState = LocalAutomation.Runtime.ExecutionTaskState;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts one shared execution task into graph-node properties for the Avalonia rendering layer.
/// </summary>
public sealed class ExecutionNodeViewModel : ViewModelBase
{
    private bool _isSelected;
    private ExecutionNodeLayout _layout = ExecutionNodeLayout.Default;

    /// <summary>
    /// Creates a graph-node view model from one shared Avalonia task view model.
    /// </summary>
    public ExecutionNodeViewModel(ExecutionTaskViewModel task)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Task.PropertyChanged += HandleTaskPropertyChanged;
    }

    /// <summary>
    /// Gets the shared task/runtime state rendered by this graph node.
    /// </summary>
    public ExecutionTaskViewModel Task { get; }

    /// <summary>
    /// Gets the stable task identifier used by the graph-selection layer.
    /// </summary>
    public RuntimeExecutionTaskId Id => Task.Id;

    /// <summary>
    /// Gets the display title rendered on the canvas and in the details pane.
    /// </summary>
    public string Title => Task.Title;

    /// <summary>
    /// Gets the descriptive text for the rendered task.
    /// </summary>
    public string Description => Task.Description;

    /// <summary>
    /// Gets the parent grouping identifier when one exists.
    /// </summary>
    public RuntimeExecutionTaskId? ParentId => Task.ParentId;

    /// <summary>
    /// Gets whether this graph node currently acts as a visual container around child tasks.
    /// </summary>
    public bool IsContainer => _layout.IsContainer;

    /// <summary>
    /// Gets or sets the x coordinate assigned by the simple auto-layout pass.
    /// </summary>
    public double X
    {
        get => _layout.X;
    }

    /// <summary>
    /// Gets or sets the y coordinate assigned by the simple auto-layout pass.
    /// </summary>
    public double Y
    {
        get => _layout.Y;
    }

    /// <summary>
    /// Gets or sets the rendered width for this node or group container.
    /// </summary>
    public double Width
    {
        get => _layout.Width;
    }

    /// <summary>
    /// Gets or sets the rendered height for this node or group container.
    /// </summary>
    public double Height
    {
        get => _layout.Height;
    }

    /// <summary>
    /// Gets the raw execution state.
    /// </summary>
    public RuntimeExecutionTaskState State => Task.State;

    /// <summary>
    /// Gets the semantic outcome once known.
    /// </summary>
    public RuntimeExecutionTaskOutcome? Outcome => Task.Outcome;

    /// <summary>
    /// Gets the combined display status rendered by the graph surfaces.
    /// </summary>
    public ExecutionTaskDisplayStatus Status => Task.Status;

    /// <summary>
    /// Gets the primary semantic status shown on the graph. Lifecycle remains available separately for animation and
    /// active-work context.
    /// </summary>
    public ExecutionTaskDisplayStatus DisplayStatus => Task.DisplayStatus;

    /// <summary>
    /// Gets the number of direct child items rendered inside this group.
    /// </summary>
    public int DirectChildCount
    {
        get => _layout.DirectChildCount;
    }

    /// <summary>
    /// Gets the number of descendant non-group tasks represented by this group.
    /// </summary>
    public int DescendantTaskCount
    {
        get => _layout.DescendantTaskCount;
    }

    /// <summary>
    /// Gets the short summary rendered in group headers and details panes.
    /// </summary>
    public string SummaryText
    {
        get => _layout.SummaryText;
    }

    /// <summary>
    /// Gets or sets whether this graph node is currently selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                RaiseSelectionVisualChanged();
            }
        }
    }

    /// <summary>
    /// Gets a short uppercase status label rendered on the graph node card.
    /// </summary>
    public string StatusText => ExecutionTaskStatusDisplay.GetUpperLabel(DisplayStatus);

    /// <summary>
    /// Gets the compact label used by the stacked dot-and-label status treatment.
    /// </summary>
    public string StatusLabelText => ExecutionTaskStatusDisplay.GetLabel(DisplayStatus);

    /// <summary>
    /// Gets the shared execution metrics currently displayed for this node.
    /// </summary>
    public RuntimeExecutionTaskMetrics Metrics => Task.Metrics;

    /// <summary>
    /// Gets the border thickness for group containers.
    /// </summary>
    public Thickness ContainerBorderThickness => new(IsSelected ? 2.0 : 1.5);

    /// <summary>
    /// Gets the border thickness for leaf task cards.
    /// </summary>
    public Thickness CardBorderThickness => new(IsSelected ? 2.0 : 1.5);

    /// <summary>
    /// Gets the description text best suited for the details pane.
    /// </summary>
    public string DetailsText
    {
        get
        {
            if (!IsContainer)
            {
                return Description;
            }

            if (string.IsNullOrWhiteSpace(Description))
            {
                return SummaryText;
            }

            if (string.IsNullOrWhiteSpace(SummaryText))
            {
                return Description;
            }

            return Description + Environment.NewLine + SummaryText;
        }
    }

    /// <summary>
    /// Gets the compact hierarchy metadata shown under group titles.
    /// </summary>
    public string GroupMetaText
    {
        get
        {
            if (!IsContainer)
            {
                return string.Empty;
            }

            return $"{DirectChildCount} child items  •  {DescendantTaskCount} runnable tasks";
        }
    }

    /// <summary>
    /// Gets whether the group metadata line should be shown.
    /// </summary>
    public bool HasGroupMetaText => !string.IsNullOrWhiteSpace(GroupMetaText);

    /// <summary>
    /// Gets a trimmed one-line description for leaf cards so the graph stays readable even with long task text.
    /// </summary>
    public string CardDescriptionText => string.IsNullOrWhiteSpace(Description)
        ? string.Empty
        : Description.Trim();

    /// <summary>
    /// Gets whether the leaf-card description row should be shown.
    /// </summary>
    public bool HasCardDescription => !string.IsNullOrWhiteSpace(CardDescriptionText);

    /// <summary>
    /// Gets the short suffix used to identify a task inside the current hierarchy without flooding the card with the
    /// full generated identifier.
    /// </summary>
    public string ShortIdText
    {
        get
        {
            string value = Id.Value;
            int lastDashIndex = value.LastIndexOf('-');
            return lastDashIndex >= 0 && lastDashIndex < value.Length - 1
                ? value[(lastDashIndex + 1)..]
                : value;
        }
    }

    /// <summary>
    /// Applies the latest layout snapshot produced by the dedicated graph-layout layer.
    /// </summary>
    internal void ApplyLayout(ExecutionNodeLayout layout)
    {
        if (layout == null)
        {
            throw new ArgumentNullException(nameof(layout));
        }

        bool boundsChanged = !layout.Equals(_layout) &&
            (Math.Abs(_layout.X - layout.X) > 0.001 ||
             Math.Abs(_layout.Y - layout.Y) > 0.001 ||
             Math.Abs(_layout.Width - layout.Width) > 0.001 ||
             Math.Abs(_layout.Height - layout.Height) > 0.001);
        bool hierarchyChanged = !layout.Equals(_layout) &&
            (_layout.IsContainer != layout.IsContainer ||
             _layout.DirectChildCount != layout.DirectChildCount ||
             _layout.DescendantTaskCount != layout.DescendantTaskCount ||
             !string.Equals(_layout.SummaryText, layout.SummaryText, StringComparison.Ordinal));

        _layout = layout;
        if (boundsChanged)
        {
            RaiseBoundsChanged();
        }

        if (!hierarchyChanged)
        {
            return;
        }

        RaisePropertyChanged(nameof(IsContainer));
        RaisePropertyChanged(nameof(DirectChildCount));
        RaisePropertyChanged(nameof(DescendantTaskCount));
        RaisePropertyChanged(nameof(SummaryText));
        RaisePropertyChanged(nameof(DetailsText));
        RaisePropertyChanged(nameof(GroupMetaText));
        RaisePropertyChanged(nameof(HasGroupMetaText));
    }

    /// <summary>
    /// Relays shared task/runtime property changes into the graph-node properties that project them.
    /// </summary>
    private void HandleTaskPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionNodeViewModel.Task.PropertyChanged")
            .SetTag("task.id", Task.Id.Value)
            .SetTag("task.title", Task.Title)
            .SetTag("property.name", e.PropertyName ?? string.Empty);

        if (string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.State), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.Outcome), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.Status), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.DisplayStatus), StringComparison.Ordinal))
        {
            RaiseStatusChanged();
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.Metrics), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(Metrics));
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.Title), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(Title));
            return;
        }

        if (string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.Description), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(Description));
            RaisePropertyChanged(nameof(CardDescriptionText));
            RaisePropertyChanged(nameof(HasCardDescription));
            RaisePropertyChanged(nameof(DetailsText));
        }
    }

    /// <summary>
    /// Raises derived graph-node status properties after the runtime state changes.
    /// </summary>
    private void RaiseStatusChanged()
    {
        /* Graph surfaces still listen for Status changes, so relay both lifecycle and semantic-display updates when either
           source changes on the shared task view model. */
        RaisePropertyChanged(nameof(Status));
        RaisePropertyChanged(nameof(State));
        RaisePropertyChanged(nameof(Outcome));
        RaisePropertyChanged(nameof(DisplayStatus));
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(StatusLabelText));
    }

    /// <summary>
    /// Raises the derived geometry properties used by the canvas and edges.
    /// </summary>
    private void RaiseBoundsChanged()
    {
        RaisePropertyChanged(nameof(X));
        RaisePropertyChanged(nameof(Y));
        RaisePropertyChanged(nameof(Width));
        RaisePropertyChanged(nameof(Height));
    }

    /// <summary>
    /// Raises derived selection-sensitive visual properties for generated canvas bindings.
    /// </summary>
    private void RaiseSelectionVisualChanged()
    {
        RaisePropertyChanged(nameof(ContainerBorderThickness));
        RaisePropertyChanged(nameof(CardBorderThickness));
    }
}
