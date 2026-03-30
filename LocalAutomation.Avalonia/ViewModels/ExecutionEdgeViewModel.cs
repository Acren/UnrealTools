using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Media;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Represents one rendered dependency edge between two runnable graph nodes after layout coordinates have been
/// assigned.
/// </summary>
public sealed class ExecutionEdgeViewModel : ViewModelBase
{
    /// <summary>
    /// Creates a rendered dependency edge between two positioned graph nodes.
    /// </summary>
    public ExecutionEdgeViewModel(ExecutionNodeViewModel source, ExecutionNodeViewModel target)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));

        // Edge geometry and color are derived from the positioned graph-node endpoints, so the edge listens to node
        // changes and republishes the affected properties for the canvas.
        Source.PropertyChanged += HandleEndpointPropertyChanged;
        Target.PropertyChanged += HandleEndpointPropertyChanged;
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
    /// Gets the first-pass elbow path used to render the dependency edge.
    /// </summary>
    public Geometry PathData
    {
        get
        {
            Point start = new(Source.X + Source.Width, Source.Y + (Source.Height / 2.0));
            Point end = new(Target.X, Target.Y + (Target.Height / 2.0));
            double midpointX = start.X + Math.Max(28, (end.X - start.X) / 2.0);
            StreamGeometry geometry = new();
            using StreamGeometryContext context = geometry.Open();
            context.BeginFigure(start, false);
            context.LineTo(new Point(midpointX, start.Y));
            context.LineTo(new Point(midpointX, end.Y));
            context.LineTo(end);
            return geometry;
        }
    }

    /// <summary>
    /// Gets the stroke color used for the edge.
    /// </summary>
    public string Stroke => Target.StatusBrush;

    /// <summary>
    /// Raises the derived edge properties when either endpoint moves, resizes, or changes status.
    /// </summary>
    private void HandleEndpointPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.X), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.Y), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.Width), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.Height), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.StatusBrush), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(PathData));
            RaisePropertyChanged(nameof(Stroke));
        }
    }
}
