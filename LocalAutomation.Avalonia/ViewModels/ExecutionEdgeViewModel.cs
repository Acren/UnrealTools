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

            SafeSpan sourceHorizontalSpan = GetSafeHorizontalSpan(Source);
            SafeSpan targetHorizontalSpan = GetSafeHorizontalSpan(Target);
            SafeSpan sourceVerticalSpan = GetSafeVerticalSpan(Source);
            SafeSpan targetVerticalSpan = GetSafeVerticalSpan(Target);

            RoutedCoordinatePair verticalRoute = ResolveRouteCoordinates(sourceVerticalSpan, targetVerticalSpan);
            if (verticalRoute.IsShared)
            {
                /* When the safe vertical spans overlap, keep the dependency as one straight horizontal segment through
                   the shared band. This branch and the elbow fallback both use the same interval pairing so small layout
                   nudges cannot flip the route onto the far edge of either node. */
                Point start = new(Source.X + Source.Width, verticalRoute.Source);
                Point end = new(Target.X, verticalRoute.Target);
                context.BeginFigure(start, false);
                context.LineTo(end);
                return geometry;
            }

            RoutedCoordinatePair horizontalRoute = ResolveRouteCoordinates(sourceHorizontalSpan, targetHorizontalSpan);
            if (horizontalRoute.IsShared)
            {
                /* When the safe horizontal spans overlap instead, keep the route as one straight vertical segment. The
                   vertical interval pairing already tells us whether the source sits above or below the target, so the
                   segment can still touch the correct outer faces instead of anchoring to the far side after recentering. */
                bool sourceIsAboveTarget = verticalRoute.Source <= verticalRoute.Target;
                Point start = sourceIsAboveTarget
                    ? new Point(horizontalRoute.Source, Source.Y + Source.Height)
                    : new Point(horizontalRoute.Source, Source.Y);
                Point end = sourceIsAboveTarget
                    ? new Point(horizontalRoute.Target, Target.Y)
                    : new Point(horizontalRoute.Target, Target.Y + Target.Height);
                context.BeginFigure(start, false);
                context.LineTo(end);
                return geometry;
            }

            /* When neither axis overlaps, connect the nearest safe endpoints picked by the same interval rule used for
               the straight-line cases. That keeps the elbow attached to the closest faces even when stage-centering or
               measured-width updates move nodes just across an overlap threshold. */
            Point elbowStart = new(Source.X + Source.Width, verticalRoute.Source);
            Point elbowEnd = new(Target.X, verticalRoute.Target);
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
    /// Returns the safe horizontal routing span for one node.
    /// </summary>
    private static SafeSpan GetSafeHorizontalSpan(ExecutionNodeViewModel node)
    {
        return new SafeSpan(GetSafeLeft(node), GetSafeRight(node));
    }

    /// <summary>
    /// Returns the safe vertical routing span for one node.
    /// </summary>
    private static SafeSpan GetSafeVerticalSpan(ExecutionNodeViewModel node)
    {
        return new SafeSpan(GetSafeTop(node), GetSafeBottom(node));
    }

    /// <summary>
    /// Chooses one shared coordinate inside the overlap span while biasing toward the two nodes' natural face centers.
    /// </summary>
    private static double ResolveSharedCoordinate(double overlapStart, double overlapEnd, double sourcePreferred, double targetPreferred)
    {
        /* Clamping the average preferred coordinate shortens visible detours for mismatched node sizes while still
           guaranteeing a stable point inside the shared safe span. */
        double preferred = (sourcePreferred + targetPreferred) / 2.0;
        return Math.Clamp(preferred, overlapStart, overlapEnd);
    }

    /// <summary>
    /// Resolves the pair of coordinates that should be connected along one axis, returning a shared point when the safe
    /// spans overlap and the nearest interval endpoints when they do not.
    /// </summary>
    private static RoutedCoordinatePair ResolveRouteCoordinates(SafeSpan sourceSpan, SafeSpan targetSpan)
    {
        if (sourceSpan.Overlaps(targetSpan))
        {
            double shared = ResolveSharedCoordinate(
                Math.Max(sourceSpan.Start, targetSpan.Start),
                Math.Min(sourceSpan.End, targetSpan.End),
                sourceSpan.Center,
                targetSpan.Center);
            return new RoutedCoordinatePair(shared, shared);
        }

        return sourceSpan.End < targetSpan.Start
            ? new RoutedCoordinatePair(sourceSpan.End, targetSpan.Start)
            : new RoutedCoordinatePair(sourceSpan.Start, targetSpan.End);
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

    /// <summary>
    /// Represents one buffered face span on a single axis so routing can reason about overlap and nearest endpoints with
    /// the same interval math everywhere.
    /// </summary>
    private readonly struct SafeSpan
    {
        public SafeSpan(double start, double end)
        {
            Start = Math.Min(start, end);
            End = Math.Max(start, end);
        }

        public double Start { get; }

        public double End { get; }

        public double Center => Start + ((End - Start) / 2.0);

        public bool Overlaps(SafeSpan other)
        {
            return Start <= other.End && other.Start <= End;
        }
    }

    /// <summary>
    /// Carries the source and target coordinates chosen for one routing axis so straight and elbow routes can share the
    /// same interval resolution result.
    /// </summary>
    private readonly struct RoutedCoordinatePair
    {
        public RoutedCoordinatePair(double source, double target)
        {
            Source = source;
            Target = target;
        }

        public double Source { get; }

        public double Target { get; }

        public bool IsShared => Math.Abs(Source - Target) <= 0.001;
    }
}
