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
    /* Keep dependency lines away from rounded corners so straight segments only appear when there is enough shared face
       between two nodes to read as an intentional edge rather than a corner collision. */
    private const double CornerBuffer = 16.0;

    /* Preserve the existing elbow spacing so fallback routing still leaves enough room for the first horizontal segment
       before turning vertical. */
    private const double ElbowHorizontalInset = 28.0;

    /// <summary>
    /// Creates a rendered dependency edge between two positioned graph nodes.
    /// </summary>
    public ExecutionEdgeViewModel(ExecutionNodeViewModel source, ExecutionNodeViewModel target)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));

        // Edge geometry is derived from the positioned graph-node endpoints, so the edge listens to node changes and
        // republishes the affected path data for the canvas.
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
    /// Gets the dependency path between two laid out nodes. When the nodes share enough buffered vertical face, the edge
    /// stays as one straight horizontal segment. Once that shared span disappears, the edge falls back to the existing
    /// elbow route using the same buffered edge points so the transition does not jump to unrelated anchor positions.
    /// </summary>
    public Geometry PathData
    {
        get
        {
            StreamGeometry geometry = new();
            using StreamGeometryContext context = geometry.Open();

            double sharedLeft = Math.Max(GetSafeLeft(Source), GetSafeLeft(Target));
            double sharedRight = Math.Min(GetSafeRight(Source), GetSafeRight(Target));
            bool nodesAreVerticallySeparated = Source.Y + Source.Height <= Target.Y || Target.Y + Target.Height <= Source.Y;
            if (nodesAreVerticallySeparated && sharedLeft <= sharedRight)
            {
                /* When stacked nodes expose a shared buffered horizontal span, route through the midpoint of that span so
                   the connection reads as one clean vertical segment instead of an elbow that can cross header chrome. */
                double sharedX = sharedLeft + ((sharedRight - sharedLeft) / 2.0);
                bool sourceIsAboveTarget = Source.Y + Source.Height <= Target.Y;
                Point start = sourceIsAboveTarget
                    ? new Point(sharedX, Source.Y + Source.Height)
                    : new Point(sharedX, Source.Y);
                Point end = sourceIsAboveTarget
                    ? new Point(sharedX, Target.Y)
                    : new Point(sharedX, Target.Y + Target.Height);
                context.BeginFigure(start, false);
                context.LineTo(end);
                return geometry;
            }

            double sourceY = ResolveSafePathY(Source);
            double targetY = ResolveSafePathY(Target);
            double sharedTop = Math.Max(sourceY, targetY);
            double sharedBottom = Math.Min(GetSafeBottom(Source), GetSafeBottom(Target));

            if (sharedTop <= sharedBottom)
            {
                /* When both nodes expose a shared buffered vertical span, route the edge through the midpoint of that
                   shared span so the connection reads as one clean horizontal line. */
                double sharedY = sharedTop + ((sharedBottom - sharedTop) / 2.0);
                Point start = new(Source.X + Source.Width, sharedY);
                Point end = new(Target.X, sharedY);
                context.BeginFigure(start, false);
                context.LineTo(end);
                return geometry;
            }

            /* Once the safe spans no longer overlap, keep using the nearest buffered edges as the anchor points so the
               line transitions into an elbow without suddenly jumping to different parts of either node. */
            Point elbowStart;
            Point elbowEnd;
            if (Source.Y + Source.Height <= Target.Y)
            {
                elbowStart = new Point(Source.X + Source.Width, GetSafeBottom(Source));
                elbowEnd = new Point(Target.X, GetSafeTop(Target));
            }
            else
            {
                elbowStart = new Point(Source.X + Source.Width, GetSafeTop(Source));
                elbowEnd = new Point(Target.X, GetSafeBottom(Target));
            }

            double midpointX = elbowStart.X + Math.Max(ElbowHorizontalInset, (elbowEnd.X - elbowStart.X) / 2.0);
            context.BeginFigure(elbowStart, false);
            context.LineTo(new Point(midpointX, elbowStart.Y));
            context.LineTo(new Point(midpointX, elbowEnd.Y));
            context.LineTo(elbowEnd);
            return geometry;
        }
    }

    /// <summary>
    /// Returns the safe top edge for routing after applying the rounded-corner buffer.
    /// </summary>
    private static double GetSafeTop(ExecutionNodeViewModel node)
    {
        double buffer = GetResolvedCornerBuffer(node);
        return node.Y + buffer;
    }

    /// <summary>
    /// Returns the safe bottom edge for routing after applying the rounded-corner buffer.
    /// </summary>
    private static double GetSafeBottom(ExecutionNodeViewModel node)
    {
        double buffer = GetResolvedCornerBuffer(node);
        return node.Y + node.Height - buffer;
    }

    /// <summary>
    /// Returns the safe left edge for routing after applying the rounded-corner buffer.
    /// </summary>
    private static double GetSafeLeft(ExecutionNodeViewModel node)
    {
        double buffer = GetResolvedCornerBuffer(node);
        return node.X + buffer;
    }

    /// <summary>
    /// Returns the safe right edge for routing after applying the rounded-corner buffer.
    /// </summary>
    private static double GetSafeRight(ExecutionNodeViewModel node)
    {
        double buffer = GetResolvedCornerBuffer(node);
        return node.X + node.Width - buffer;
    }

    /// <summary>
    /// Resolves the top safe boundary because the shared-span computation works in absolute graph-space Y values.
    /// </summary>
    private static double ResolveSafePathY(ExecutionNodeViewModel node)
    {
        return GetSafeTop(node);
    }

    /// <summary>
    /// Shrinks the corner buffer for short nodes so the safe routing span never inverts.
    /// </summary>
    private static double GetResolvedCornerBuffer(ExecutionNodeViewModel node)
    {
        return Math.Min(CornerBuffer, Math.Max(0.0, (node.Height / 2.0) - 1.0));
    }

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
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.Status), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.DisplayStatus), StringComparison.Ordinal))
        {
            RaisePropertyChanged(nameof(PathData));
        }
    }
}
