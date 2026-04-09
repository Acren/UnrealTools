using System;
using Avalonia;
using Avalonia.Media;
using LocalAutomation.Avalonia.ExecutionGraph;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Represents one rendered dependency edge between two visible graph nodes using geometry produced by the dedicated
/// graph-layout layer.
/// </summary>
public sealed class ExecutionEdgeViewModel : ViewModelBase
{
    private ExecutionGraphEdgeLayout _layout;

    /// <summary>
    /// Creates a rendered dependency edge between two positioned graph nodes.
    /// </summary>
    internal ExecutionEdgeViewModel(ExecutionNodeViewModel source, ExecutionNodeViewModel target, ExecutionGraphEdgeLayout layout)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    /// <summary>
    /// Gets the source graph node.
    /// </summary>
    public ExecutionNodeViewModel Source { get; }

    /// <summary>
    /// Gets the target graph node.
    /// </summary>
    public ExecutionNodeViewModel Target { get; }

    /// <summary>
    /// Gets the dependency path between two laid out nodes.
    /// </summary>
    public Geometry PathData
    {
        get
        {
            StreamGeometry geometry = new();
            using StreamGeometryContext context = geometry.Open();
            ExecutionGraphPoint firstPoint = _layout.Route.Points[0];
            context.BeginFigure(new Point(firstPoint.X, firstPoint.Y), false);
            for (int index = 1; index < _layout.Route.Points.Count; index++)
            {
                ExecutionGraphPoint point = _layout.Route.Points[index];
                context.LineTo(new Point(point.X, point.Y));
            }

            return geometry;
        }
    }

    /// <summary>
    /// Applies the latest routed edge snapshot produced by the graph-layout layer.
    /// </summary>
    internal void ApplyLayout(ExecutionGraphEdgeLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        RaisePropertyChanged(nameof(PathData));
    }
}
