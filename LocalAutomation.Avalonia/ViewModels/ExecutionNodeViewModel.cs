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
    private ExecutionTaskStatus _status;
    private string _statusReason;
    private string _summaryText = string.Empty;
    private bool _isSelected;
    private double _x;
    private double _y;
    private double _width = ExecutionGraphViewModel.NodeWidth;
    private double _height = ExecutionGraphViewModel.NodeHeight;
    private int _directChildCount;
    private int _descendantTaskCount;

    /// <summary>
    /// Creates a graph-node view model from the shared execution-task model.
    /// </summary>
    public ExecutionNodeViewModel(ExecutionTask task)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        _status = task.Status;
        _statusReason = task.StatusReason;
    }

    /// <summary>
    /// Gets the underlying shared execution task that this graph node renders.
    /// </summary>
    public ExecutionTask Task { get; }

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
    /// Gets the shared task kind used for subtle visual treatment changes.
    /// </summary>
    public ExecutionTaskKind Kind => Task.Kind;

    /// <summary>
    /// Gets whether this rendered graph node represents a grouping task.
    /// </summary>
    public bool IsGroup => Kind == ExecutionTaskKind.Group;

    /// <summary>
    /// Gets whether this graph node currently acts as a visual container around child tasks.
    /// </summary>
    public bool IsContainer => IsGroup && DirectChildCount > 0;

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
    public ExecutionTaskStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value))
            {
                return;
            }

            RaiseStatusChanged();
        }
    }

    /// <summary>
    /// Gets the explanatory text for disabled, blocked, failed, or otherwise notable states.
    /// </summary>
    public string StatusReason
    {
        get => _statusReason;
        private set => SetProperty(ref _statusReason, value);
    }

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
    public string StatusText => Status switch
    {
        ExecutionTaskStatus.Planned => "PLANNED",
        ExecutionTaskStatus.Pending => "PENDING",
        ExecutionTaskStatus.Blocked => "BLOCKED",
        ExecutionTaskStatus.Running => "RUNNING",
        ExecutionTaskStatus.Completed => "DONE",
        ExecutionTaskStatus.Failed => "FAILED",
        ExecutionTaskStatus.Skipped => "SKIPPED",
        ExecutionTaskStatus.Disabled => "DISABLED",
        ExecutionTaskStatus.Cancelled => "CANCELLED",
        _ => Status.ToString().ToUpperInvariant()
    };

    /// <summary>
    /// Gets the brush key used to tint the graph node status pill and dependency accents.
    /// </summary>
    public string StatusBrush => Status switch
    {
        // Match the execution graph accents to the runtime tab marker palette where the status concepts overlap so the
        // workspace reads like one system instead of two adjacent visual languages.
        ExecutionTaskStatus.Completed => "#7BE37B",
        ExecutionTaskStatus.Running => "#63A8FF",
        ExecutionTaskStatus.Failed => "#FF5C5C",
        ExecutionTaskStatus.Disabled => "#89919A",
        ExecutionTaskStatus.Pending => "#89919A",
        ExecutionTaskStatus.Blocked => "#F1D479",
        ExecutionTaskStatus.Cancelled => "#C77DFF",
        ExecutionTaskStatus.Skipped => "#89919A",
        _ => "#89919A"
    };

    /// <summary>
    /// Gets the status brush as a live Avalonia brush for generated canvas bindings.
    /// </summary>
    public IBrush StatusBrushColor => new SolidColorBrush(Color.Parse(StatusBrush));

    /// <summary>
    /// Gets the compact label used by the stacked dot-and-label status treatment.
    /// </summary>
    public string StatusLabelText => Status switch
    {
        ExecutionTaskStatus.Completed => "Done",
        ExecutionTaskStatus.Pending => "Pending",
        ExecutionTaskStatus.Blocked => "Blocked",
        ExecutionTaskStatus.Running => "Running",
        ExecutionTaskStatus.Failed => "Failed",
        ExecutionTaskStatus.Skipped => "Skipped",
        ExecutionTaskStatus.Disabled => "Disabled",
        ExecutionTaskStatus.Cancelled => "Cancelled",
        ExecutionTaskStatus.Planned => "Planned",
        _ => Status.ToString()
    };

    /// <summary>
    /// Gets the softer foreground brush used by the stacked status label.
    /// </summary>
    public IBrush StatusLabelBrush => new SolidColorBrush(Color.Parse("#89919A"));

    /// <summary>
    /// Gets the compact status dot brush used by the stacked dot-and-label treatment.
    /// </summary>
    public IBrush StatusDotBrush => StatusBrushColor;

    /// <summary>
    /// Gets the subtle glow used by the graph status dot so active and terminal states feel consistent with the tab
    /// strip markers.
    /// </summary>
    public BoxShadows StatusDotShadow => Status switch
    {
        ExecutionTaskStatus.Running => BoxShadows.Parse("0 0 5 0 #5563A8FF"),
        ExecutionTaskStatus.Completed => BoxShadows.Parse("0 0 5 0 #557BE37B"),
        ExecutionTaskStatus.Failed => BoxShadows.Parse("0 0 5 0 #55FF7575"),
        ExecutionTaskStatus.Blocked => BoxShadows.Parse("0 0 5 0 #55F1D479"),
        _ => BoxShadows.Parse("0 0 0 0 Transparent")
    };

    /// <summary>
    /// Gets the lighter outline used for group container borders.
    /// </summary>
    public IBrush ContainerBorderBrush => new SolidColorBrush(Color.Parse(ApplyAlpha(StatusBrush, IsSelected ? "AA" : "5C")));

    /// <summary>
    /// Gets the background brush for a group container body.
    /// </summary>
    public IBrush ContainerBackgroundBrush => new SolidColorBrush(Color.Parse(IsSelected ? "#1A2430" : "#131A24"));

    /// <summary>
    /// Gets the background brush for a group header strip.
    /// </summary>
    public IBrush GroupHeaderBackgroundBrush => new SolidColorBrush(Color.Parse(IsSelected ? "#202D3B" : "#182230"));

    /// <summary>
    /// Gets the background brush for a leaf task card.
    /// </summary>
    public IBrush CardBackgroundBrush => new SolidColorBrush(Color.Parse(IsSelected ? "#263342" : "#1D2835"));

    /// <summary>
    /// Gets the shadow applied to the rendered node when selection should remain visible on the canvas.
    /// </summary>
    public BoxShadows CardShadow => IsSelected
        ? BoxShadows.Parse("0 0 0 1 #6AA7C2D8, 0 14 28 0 #26000000")
        : BoxShadows.Parse(IsContainer ? "0 10 24 0 #14000000" : "0 14 26 0 #24000000");

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
        Status = status;
        StatusReason = statusReason ?? string.Empty;
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
    /// Raises derived graph-node status properties after the runtime state changes.
    /// </summary>
    private void RaiseStatusChanged()
    {
        RaisePropertyChanged(nameof(StatusText));
        RaisePropertyChanged(nameof(StatusBrush));
        RaisePropertyChanged(nameof(StatusBrushColor));
        RaisePropertyChanged(nameof(StatusLabelText));
        RaisePropertyChanged(nameof(StatusLabelBrush));
        RaisePropertyChanged(nameof(StatusDotBrush));
        RaisePropertyChanged(nameof(StatusDotShadow));
        RaisePropertyChanged(nameof(ContainerBorderBrush));
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
        RaisePropertyChanged(nameof(ContainerBorderBrush));
        RaisePropertyChanged(nameof(ContainerBackgroundBrush));
        RaisePropertyChanged(nameof(GroupHeaderBackgroundBrush));
        RaisePropertyChanged(nameof(CardBackgroundBrush));
        RaisePropertyChanged(nameof(CardShadow));
        RaisePropertyChanged(nameof(ContainerBorderThickness));
        RaisePropertyChanged(nameof(CardBorderThickness));
    }

    private static string ApplyAlpha(string hexColor, string alpha)
    {
        return hexColor.StartsWith("#", StringComparison.Ordinal) && hexColor.Length == 7
            ? $"#{alpha}{hexColor[1..]}"
            : hexColor;
    }
}
