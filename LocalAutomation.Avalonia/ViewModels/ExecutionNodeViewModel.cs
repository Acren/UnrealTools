using System;
using LocalAutomation.Avalonia.ExecutionGraph;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;

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
    /// Gets or sets whether this graph node is currently selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
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
        bool containerChanged = !layout.Equals(_layout) && _layout.IsContainer != layout.IsContainer;

        _layout = layout;
        if (boundsChanged)
        {
            RaiseBoundsChanged();
        }

        if (!containerChanged)
        {
            return;
        }

        RaisePropertyChanged(nameof(IsContainer));
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

}
