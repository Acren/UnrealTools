using System;
using Avalonia;
using Avalonia.Media;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts one shared execution task into graph-node properties for the Avalonia rendering layer.
/// </summary>
public sealed class ExecutionNodeViewModel : ViewModelBase
{
    private string _summaryText = string.Empty;
    private bool _isSelected;
    private double _x;
    private double _y;
    private double _width = ExecutionGraphViewModel.NodeMinWidth;
    private double _height = ExecutionGraphViewModel.NodeHeight;
    private int _directChildCount;
    private int _descendantTaskCount;

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
    public ExecutionTaskId Id => Task.Id;

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
    public ExecutionTaskId? ParentId => Task.ParentId;

    /// <summary>
    /// Gets whether this graph node currently acts as a visual container around child tasks.
    /// </summary>
    public bool IsContainer => DirectChildCount > 0;

    /// <summary>
    /// Gets or sets the x coordinate assigned by the simple auto-layout pass.
    /// </summary>
    public double X
    {
        get => _x;
        set
        {
            if (SetProperty(ref _x, value))
            {
                RaiseBoundsChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the y coordinate assigned by the simple auto-layout pass.
    /// </summary>
    public double Y
    {
        get => _y;
        set
        {
            if (SetProperty(ref _y, value))
            {
                RaiseBoundsChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the rendered width for this node or group container.
    /// </summary>
    public double Width
    {
        get => _width;
        private set
        {
            if (SetProperty(ref _width, value))
            {
                RaiseBoundsChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the rendered height for this node or group container.
    /// </summary>
    public double Height
    {
        get => _height;
        private set
        {
            if (SetProperty(ref _height, value))
            {
                RaiseBoundsChanged();
            }
        }
    }

    /// <summary>
    /// Gets the current runtime or preview status shown on the graph.
    /// </summary>
    public ExecutionTaskStatus Status => Task.Status;

    /// <summary>
    /// Gets the number of direct child items rendered inside this group.
    /// </summary>
    public int DirectChildCount
    {
        get => _directChildCount;
        private set
        {
            if (SetProperty(ref _directChildCount, value))
            {
                RaisePropertyChanged(nameof(IsContainer));
            }
        }
    }

    /// <summary>
    /// Gets the number of descendant non-group tasks represented by this group.
    /// </summary>
    public int DescendantTaskCount
    {
        get => _descendantTaskCount;
        private set => SetProperty(ref _descendantTaskCount, value);
    }

    /// <summary>
    /// Gets the short summary rendered in group headers and details panes.
    /// </summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
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
    public string StatusText => ExecutionTaskStatusDisplay.GetUpperLabel(Status);

    /// <summary>
    /// Gets the compact label used by the stacked dot-and-label status treatment.
    /// </summary>
    public string StatusLabelText => ExecutionTaskStatusDisplay.GetLabel(Status);

    /// <summary>
    /// Gets the shared execution metrics currently displayed for this node.
    /// </summary>
    public ExecutionTaskMetrics Metrics => Task.Metrics;

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
    /// Updates the rendered task status from live session data.
    /// </summary>
    public void SetStatus(ExecutionTaskStatus status, string? statusReason)
    {
        Task.SetStatus(status, statusReason);
    }

    /// <summary>
    /// Updates the rendered size and position used by the canvas and dependency routing.
    /// </summary>
    public void SetBounds(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Updates the group-container metrics derived from the hierarchy projection.
    /// </summary>
    public void SetHierarchyMetrics(int directChildCount, int descendantTaskCount, string? summaryText)
    {
        DirectChildCount = directChildCount;
        DescendantTaskCount = descendantTaskCount;
        SummaryText = summaryText ?? string.Empty;
        RaisePropertyChanged(nameof(DetailsText));
        RaisePropertyChanged(nameof(GroupMetaText));
        RaisePropertyChanged(nameof(HasGroupMetaText));
    }

    /// <summary>
    /// Updates the shared execution metrics shown on the node card or group header.
    /// </summary>
    public void SetMetrics(ExecutionTaskMetrics metrics)
    {
        Task.SetMetrics(metrics);
    }

    /// <summary>
    /// Relays shared task/runtime property changes into the graph-node properties that project them.
    /// </summary>
    private void HandleTaskPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.Status), StringComparison.Ordinal))
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
        /* Graph controls and edge styling still observe the node VM's raw Status property, even though the status now
           lives on the shared task VM. Relay that notification here before the derived label updates so existing graph
           listeners keep reacting to runtime status changes. */
        RaisePropertyChanged(nameof(Status));
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
