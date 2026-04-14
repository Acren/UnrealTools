using System;
using Avalonia.Media;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Core;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;

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
    /// Gets the stable logical edge identifier used by the retained canvas reconciliation path.
    /// </summary>
    public (RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId) EdgeKey => (Source.Id, Target.Id);

    /// <summary>
    /// Gets how many routed points currently define this edge geometry.
    /// </summary>
    public int RoutePointCount => _layout.Route.Points.Count;

    /// <summary>
    /// Materializes one shareable geometry instance for the current routed edge.
    /// </summary>
    public Geometry CreatePathGeometry()
    {
        /* Measure the whole geometry-materialization boundary at the edge so outer traces can continue comparing one
           edge against the retained-edge batch while the nested scopes isolate which StreamGeometry step is slow. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.MaterializeEdgeGeometry")
            .SetTag("source.id", Source.Id.Value)
            .SetTag("target.id", Target.Id.Value)
            .SetTag("route.point.count", RoutePointCount);

        StreamGeometry geometry;
        /* Separate raw geometry allocation from the later stream work so a stall can be attributed to object creation
           instead of to route emission or stream finalization. */
        using (PerformanceActivityScope allocateActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.MaterializeEdgeGeometry.AllocateGeometry"))
        {
            geometry = new StreamGeometry();
        }

        StreamGeometryContext context;
        /* Opening the context can allocate backing stream state, so measure that separately from writing the points. */
        using (PerformanceActivityScope openActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.MaterializeEdgeGeometry.OpenContext"))
        {
            context = geometry.Open();
        }

        try
        {
            /* Emit the routed figure under one child span so traces can distinguish point writing from allocation and
               close-time commit work. */
            using (PerformanceActivityScope emitActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.MaterializeEdgeGeometry.EmitRoute")
                .SetTag("segment.count", Math.Max(0, RoutePointCount - 1)))
            {
                ExecutionGraphPoint firstPoint = _layout.Route.Points[0];
                context.BeginFigure(new global::Avalonia.Point(firstPoint.X, firstPoint.Y), false);
                for (int index = 1; index < _layout.Route.Points.Count; index++)
                {
                    ExecutionGraphPoint point = _layout.Route.Points[index];
                    context.LineTo(new global::Avalonia.Point(point.X, point.Y));
                }
            }
        }
        finally
        {
            /* Closing the context can flush the buffered route into the final immutable geometry, so time that commit
               boundary independently from the earlier point writes. */
            using PerformanceActivityScope closeActivity = PerformanceTelemetry.StartActivity("ExecutionGraphCanvas.MaterializeEdgeGeometry.CloseContext");
            context.Dispose();
        }

        return geometry;
    }

    /// <summary>
    /// Applies the latest routed edge snapshot produced by the graph-layout layer.
    /// </summary>
    internal void ApplyLayout(ExecutionGraphEdgeLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }
}
