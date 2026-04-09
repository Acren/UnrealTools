using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalAutomation.Avalonia.ExecutionGraph;

/// <summary>
/// Represents one point in execution-graph world space.
/// </summary>
internal readonly record struct ExecutionGraphPoint(double X, double Y);

/// <summary>
/// Represents one world-space rectangle used by the execution-graph layout and routing layers.
/// </summary>
internal readonly struct ExecutionGraphRect
{
    /// <summary>
    /// Gets an empty rectangle value used by incremental bound aggregation.
    /// </summary>
    public static ExecutionGraphRect Empty { get; } = new(0, 0, 0, 0, isEmpty: true);

    /// <summary>
    /// Creates one world-space rectangle.
    /// </summary>
    public ExecutionGraphRect(double x, double y, double width, double height)
        : this(x, y, width, height, isEmpty: false)
    {
    }

    /// <summary>
    /// Gets the left edge.
    /// </summary>
    public double X { get; }

    /// <summary>
    /// Gets the top edge.
    /// </summary>
    public double Y { get; }

    /// <summary>
    /// Gets the rectangle width.
    /// </summary>
    public double Width { get; }

    /// <summary>
    /// Gets the rectangle height.
    /// </summary>
    public double Height { get; }

    /// <summary>
    /// Gets whether this rectangle represents the empty aggregation sentinel.
    /// </summary>
    public bool IsEmpty { get; }

    /// <summary>
    /// Gets the right edge.
    /// </summary>
    public double Right => X + Width;

    /// <summary>
    /// Gets the bottom edge.
    /// </summary>
    public double Bottom => Y + Height;

    /// <summary>
    /// Returns the union of this rectangle and another rectangle.
    /// </summary>
    public ExecutionGraphRect Include(ExecutionGraphRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        double left = Math.Min(X, other.X);
        double top = Math.Min(Y, other.Y);
        double right = Math.Max(Right, other.Right);
        double bottom = Math.Max(Bottom, other.Bottom);
        return new ExecutionGraphRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Returns whether this rectangle overlaps another non-empty rectangle.
    /// </summary>
    public bool Intersects(ExecutionGraphRect other)
    {
        if (IsEmpty || other.IsEmpty || Width <= 0 || Height <= 0 || other.Width <= 0 || other.Height <= 0)
        {
            return false;
        }

        return X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
    }

    /// <summary>
    /// Creates one rectangle from a set of route points.
    /// </summary>
    public static ExecutionGraphRect FromPoints(IReadOnlyList<ExecutionGraphPoint> points)
    {
        if (points == null)
        {
            throw new ArgumentNullException(nameof(points));
        }

        if (points.Count == 0)
        {
            return Empty;
        }

        double minX = points.Min(point => point.X);
        double minY = points.Min(point => point.Y);
        double maxX = points.Max(point => point.X);
        double maxY = points.Max(point => point.Y);
        return new ExecutionGraphRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Creates one rectangle from explicit edges.
    /// </summary>
    internal static ExecutionGraphRect FromEdges(double left, double top, double right, double bottom)
    {
        return new ExecutionGraphRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// Creates the empty sentinel or a normal rectangle value.
    /// </summary>
    private ExecutionGraphRect(double x, double y, double width, double height, bool isEmpty)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        IsEmpty = isEmpty;
    }
}

/// <summary>
/// Carries the routed points for one dependency edge after layout has chosen the actual face-to-face path.
/// </summary>
internal sealed class ExecutionGraphEdgeRoute
{
    /// <summary>
    /// Creates one immutable dependency route from the provided world-space points.
    /// </summary>
    public ExecutionGraphEdgeRoute(IReadOnlyList<ExecutionGraphPoint> points)
    {
        if (points == null)
        {
            throw new ArgumentNullException(nameof(points));
        }

        if (points.Count < 2)
        {
            throw new ArgumentException("An execution-graph edge route requires at least two points.", nameof(points));
        }

        Points = points.ToArray();
        Bounds = ExecutionGraphRect.FromPoints(Points);
    }

    /// <summary>
    /// Gets the ordered route points.
    /// </summary>
    public IReadOnlyList<ExecutionGraphPoint> Points { get; }

    /// <summary>
    /// Gets the bounding rectangle of the routed line segments.
    /// </summary>
    public ExecutionGraphRect Bounds { get; }
}
