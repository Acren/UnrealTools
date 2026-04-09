using System;
using System.Collections.Generic;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;

namespace LocalAutomation.Avalonia.ExecutionGraph;

/// <summary>
/// Carries the complete output of one execution-graph layout pass.
/// </summary>
internal sealed class ExecutionGraphLayoutResult
{
    /// <summary>
    /// Gets the empty layout result used before any graph has been projected.
    /// </summary>
    public static ExecutionGraphLayoutResult Empty { get; } = new(
        nodeLayouts: new Dictionary<RuntimeExecutionTaskId, ExecutionNodeLayout>(),
        edgeLayouts: Array.Empty<ExecutionGraphEdgeLayout>(),
        canvasWidth: ExecutionGraphLayoutSettings.NodeMinWidth,
        canvasHeight: ExecutionGraphLayoutSettings.NodeHeight,
        structureLayering: ExecutionGraphStructureLayeringSnapshot.Empty);

    /// <summary>
    /// Creates one immutable layout result.
    /// </summary>
    public ExecutionGraphLayoutResult(
        IReadOnlyDictionary<RuntimeExecutionTaskId, ExecutionNodeLayout> nodeLayouts,
        IReadOnlyList<ExecutionGraphEdgeLayout> edgeLayouts,
        double canvasWidth,
        double canvasHeight,
        ExecutionGraphStructureLayeringSnapshot structureLayering)
    {
        NodeLayouts = nodeLayouts ?? throw new ArgumentNullException(nameof(nodeLayouts));
        EdgeLayouts = edgeLayouts ?? throw new ArgumentNullException(nameof(edgeLayouts));
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        StructureLayering = structureLayering ?? throw new ArgumentNullException(nameof(structureLayering));
    }

    /// <summary>
    /// Gets the laid out node rectangles and hierarchy metrics keyed by visible task id.
    /// </summary>
    public IReadOnlyDictionary<RuntimeExecutionTaskId, ExecutionNodeLayout> NodeLayouts { get; }

    /// <summary>
    /// Gets the routed visible dependency edges for the current graph snapshot.
    /// </summary>
    public IReadOnlyList<ExecutionGraphEdgeLayout> EdgeLayouts { get; }

    /// <summary>
    /// Gets the total canvas width required to render the current graph.
    /// </summary>
    public double CanvasWidth { get; }

    /// <summary>
    /// Gets the total canvas height required to render the current graph.
    /// </summary>
    public double CanvasHeight { get; }

    /// <summary>
    /// Gets the structural layering snapshot used by the canvas to interleave groups and whole edges.
    /// </summary>
    public ExecutionGraphStructureLayeringSnapshot StructureLayering { get; }
}

/// <summary>
/// Stores the laid out rectangle and hierarchy metadata for one visible graph node.
/// </summary>
internal sealed record ExecutionNodeLayout(
    double X,
    double Y,
    double Width,
    double Height,
    bool IsContainer,
    int DirectChildCount,
    int DescendantTaskCount,
    string SummaryText)
{
    /// <summary>
    /// Gets the default placeholder layout used before the first layout pass assigns real bounds.
    /// </summary>
    public static ExecutionNodeLayout Default { get; } = new(
        X: 0,
        Y: 0,
        Width: ExecutionGraphLayoutSettings.NodeMinWidth,
        Height: ExecutionGraphLayoutSettings.NodeHeight,
        IsContainer: false,
        DirectChildCount: 0,
        DescendantTaskCount: 0,
        SummaryText: string.Empty);

    /// <summary>
    /// Gets the rectangle occupied by the laid out node.
    /// </summary>
    public ExecutionGraphRect Bounds => new(X, Y, Width, Height);
}

/// <summary>
/// Stores the routed geometry metadata for one visible dependency edge.
/// </summary>
internal sealed class ExecutionGraphEdgeLayout
{
    /// <summary>
    /// Creates one immutable visible dependency edge layout.
    /// </summary>
    public ExecutionGraphEdgeLayout(RuntimeExecutionTaskId sourceId, RuntimeExecutionTaskId targetId, ExecutionGraphEdgeRoute route)
    {
        SourceId = sourceId;
        TargetId = targetId;
        Route = route ?? throw new ArgumentNullException(nameof(route));
    }

    /// <summary>
    /// Gets the visible source node id.
    /// </summary>
    public RuntimeExecutionTaskId SourceId { get; }

    /// <summary>
    /// Gets the visible target node id.
    /// </summary>
    public RuntimeExecutionTaskId TargetId { get; }

    /// <summary>
    /// Gets the routed line path for the edge.
    /// </summary>
    public ExecutionGraphEdgeRoute Route { get; }
}

/// <summary>
/// Carries the visible group ordering plus whole-edge slot constraints for one graph snapshot.
/// </summary>
public sealed class ExecutionGraphStructureLayeringSnapshot
{
    /// <summary>
    /// Gets the empty layering snapshot used before any graph content exists.
    /// </summary>
    public static ExecutionGraphStructureLayeringSnapshot Empty { get; } = new(
        orderedGroupIds: Array.Empty<RuntimeExecutionTaskId>(),
        edgeConstraints: new Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), ExecutionGraphEdgeLayeringConstraints>());

    /// <summary>
    /// Creates one immutable layering snapshot from the current visible groups and per-edge constraints.
    /// </summary>
    public ExecutionGraphStructureLayeringSnapshot(
        IReadOnlyList<RuntimeExecutionTaskId> orderedGroupIds,
        IReadOnlyDictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), ExecutionGraphEdgeLayeringConstraints> edgeConstraints)
    {
        OrderedGroupIds = orderedGroupIds ?? throw new ArgumentNullException(nameof(orderedGroupIds));
        EdgeConstraints = edgeConstraints ?? throw new ArgumentNullException(nameof(edgeConstraints));
    }

    /// <summary>
    /// Gets visible groups in deterministic base draw order from back to front.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> OrderedGroupIds { get; }

    /// <summary>
    /// Gets the whole-edge ordering constraints keyed by visible source and target identifiers.
    /// </summary>
    public IReadOnlyDictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), ExecutionGraphEdgeLayeringConstraints> EdgeConstraints { get; }
}

/// <summary>
/// Carries the groups a whole edge must render above and below for one graph snapshot.
/// </summary>
public sealed class ExecutionGraphEdgeLayeringConstraints
{
    /// <summary>
    /// Creates one immutable set of whole-edge ordering constraints.
    /// </summary>
    public ExecutionGraphEdgeLayeringConstraints(IReadOnlyList<RuntimeExecutionTaskId> groupsToRenderAbove, IReadOnlyList<RuntimeExecutionTaskId> groupsToRenderBelow)
    {
        GroupsToRenderAbove = groupsToRenderAbove ?? throw new ArgumentNullException(nameof(groupsToRenderAbove));
        GroupsToRenderBelow = groupsToRenderBelow ?? throw new ArgumentNullException(nameof(groupsToRenderBelow));
    }

    /// <summary>
    /// Gets the visible groups that a whole edge must render above because one endpoint belongs to their subtree.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GroupsToRenderAbove { get; }

    /// <summary>
    /// Gets the visible groups that a whole edge should render below because the route crosses them without belonging to
    /// their subtree.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GroupsToRenderBelow { get; }
}
