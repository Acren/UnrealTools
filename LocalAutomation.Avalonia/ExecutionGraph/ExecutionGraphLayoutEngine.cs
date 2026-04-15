using System;
using System.Collections.Generic;
using System.Linq;
using CorePerformanceTelemetry = LocalAutomation.Core.PerformanceTelemetry;
using PerformanceActivityScope = LocalAutomation.Core.PerformanceActivityScope;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;

namespace LocalAutomation.Avalonia.ExecutionGraph;

/// <summary>
/// Computes execution-graph node bounds, dependency routes, hierarchy metrics, canvas extents, and layering policy from
/// a visible graph projection plus the current measured-width cache.
/// </summary>
internal sealed class ExecutionGraphLayoutEngine
{
    private readonly ExecutionGraphProjection _projection;
    private readonly ExecutionGraphLayoutState _layoutState;
    private readonly Dictionary<RuntimeExecutionTaskId, MutableNodeLayout> _nodeLayouts;

    /// <summary>
    /// Computes one complete execution-graph layout result.
    /// </summary>
    public static ExecutionGraphLayoutResult Calculate(ExecutionGraphProjection projection, ExecutionGraphLayoutState layoutState)
    {
        return new ExecutionGraphLayoutEngine(projection, layoutState).Calculate();
    }

    /// <summary>
    /// Creates one layout engine instance for the provided graph snapshot.
    /// </summary>
    private ExecutionGraphLayoutEngine(ExecutionGraphProjection projection, ExecutionGraphLayoutState layoutState)
    {
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _layoutState = layoutState ?? throw new ArgumentNullException(nameof(layoutState));
        _nodeLayouts = projection.VisibleTaskIds.ToDictionary(taskId => taskId, _ => new MutableNodeLayout());
    }

    /// <summary>
    /// Computes the final immutable layout result from the current projection and width cache.
    /// </summary>
    private ExecutionGraphLayoutResult Calculate()
    {
        if (_projection.VisibleTaskIds.Count == 0)
        {
            return ExecutionGraphLayoutResult.Empty;
        }

        /* The first-pass tree layout positions every visible node and subtree before any edge or layering work can run,
           so measure it separately from the later graph-derivation phases. */
        using (PerformanceActivityScope layoutTreeActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.LayoutVisibleTree")
            .SetTag("visible.node.count", _projection.VisibleTaskIds.Count)
            .SetTag("root.child.count", _projection.GetDirectChildIds(parentId: null).Count))
        {
            LayoutDirectChildren(parentId: null, originX: ExecutionGraphLayoutSettings.CanvasMargin, originY: ExecutionGraphLayoutSettings.CanvasMargin);
        }

        /* Group summaries walk the already-laid-out hierarchy without changing node positions, so keep their cost
           distinct from the structural layout pass. */
        using (PerformanceActivityScope groupMetricsActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.ApplyGroupHierarchyMetrics"))
        {
            ApplyGroupHierarchyMetrics();
        }

        List<ExecutionGraphEdgeLayout> edgeLayouts;
        /* Edge extraction resolves the visible dependency set and routing after node bounds are known, so time it apart
           from the earlier tree layout and the later layering policy. */
        using (PerformanceActivityScope buildEdgesActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.BuildDependencyEdges"))
        {
            edgeLayouts = BuildDependencyEdges();
            buildEdgesActivity.SetTag("edge.count", edgeLayouts.Count);
        }

        ExecutionGraphStructureLayeringSnapshot structureLayering;
        /* Whole-edge layering performs the broadest edge-vs-group reasoning in the layout engine, so keep it isolated
           from route generation and immutable snapshot creation. */
        using (PerformanceActivityScope layeringActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.BuildStructureLayering")
            .SetTag("edge.count", edgeLayouts.Count))
        {
            structureLayering = BuildStructureLayeringSnapshot(edgeLayouts);
            layeringActivity.SetTag("group.count", structureLayering.OrderedGroupIds.Count)
                .SetTag("edge.constraint.count", structureLayering.EdgeConstraints.Count);
        }

        Dictionary<RuntimeExecutionTaskId, ExecutionNodeLayout> nodeLayouts;
        /* The final immutable node snapshot is a separate allocation step after all mutable layout work is complete. */
        using (PerformanceActivityScope materializeNodeLayoutsActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.MaterializeNodeLayouts")
            .SetTag("node.count", _nodeLayouts.Count))
        {
            nodeLayouts = _nodeLayouts.ToDictionary(entry => entry.Key, entry => entry.Value.ToImmutable());
        }

        double canvasWidth;
        double canvasHeight;
        /* Canvas extents are derived from the immutable node snapshot, so measure them separately from snapshot
           materialization in case extent scans become significant on larger graphs. */
        using (PerformanceActivityScope canvasExtentsActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.CalculateCanvasExtents")
            .SetTag("node.count", nodeLayouts.Count))
        {
            canvasWidth = CalculateCanvasWidth(nodeLayouts.Values);
            canvasHeight = CalculateCanvasHeight(nodeLayouts.Values);
            canvasExtentsActivity.SetTag("canvas.width", canvasWidth)
                .SetTag("canvas.height", canvasHeight);
        }

        return new ExecutionGraphLayoutResult(
            nodeLayouts,
            edgeLayouts,
            canvasWidth,
            canvasHeight,
            structureLayering);
    }

    /// <summary>
    /// Lays out one sibling set as dependency stages: sequential stages flow left-to-right while siblings that can run in
    /// parallel within the same stage stack vertically.
    /// </summary>
    private ExecutionGraphRect LayoutDirectChildren(RuntimeExecutionTaskId? parentId, double originX, double originY)
    {
        IReadOnlyList<RuntimeExecutionTaskId> childIds = _projection.GetDirectChildIds(parentId);
        if (childIds.Count == 0)
        {
            return ExecutionGraphRect.Empty;
        }

        IReadOnlyList<IReadOnlyList<RuntimeExecutionTaskId>> stages = BuildSiblingStages(childIds);
        if (stages.Count == 1)
        {
            return LayoutChildrenVertically(childIds, originX, originY);
        }

        return LayoutSiblingStages(stages, originX, originY);
    }

    /// <summary>
    /// Partitions one sibling set into dependency stages so siblings with no remaining intra-sibling dependencies share a
    /// stage and later dependents flow into columns to the right.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<RuntimeExecutionTaskId>> BuildSiblingStages(IReadOnlyList<RuntimeExecutionTaskId> childIds)
    {
        if (childIds.Count <= 1)
        {
            return new[] { childIds };
        }

        HashSet<RuntimeExecutionTaskId> visibleChildIds = childIds.ToHashSet();
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> siblingOwnerByVisibleNodeId = BuildSiblingOwnerMap(childIds);
        Dictionary<RuntimeExecutionTaskId, HashSet<RuntimeExecutionTaskId>> remainingDependencies = childIds.ToDictionary(
            childId => childId,
            childId => _projection.GetVisibleDependencyIds(childId)
                .Select(dependencyId => siblingOwnerByVisibleNodeId.TryGetValue(dependencyId, out RuntimeExecutionTaskId ownerId) ? ownerId : default)
                .Where(ownerId => ownerId != default && ownerId != childId && visibleChildIds.Contains(ownerId))
                .ToHashSet());
        List<RuntimeExecutionTaskId> remainingChildIds = childIds.ToList();
        List<IReadOnlyList<RuntimeExecutionTaskId>> stages = new();

        while (remainingChildIds.Count > 0)
        {
            List<RuntimeExecutionTaskId> stage = remainingChildIds
                .Where(childId => remainingDependencies[childId].Count == 0)
                .ToList();

            if (stage.Count == 0)
            {
                /* Valid execution plans are DAGs, so this fallback should only protect the UI from malformed data. When a
                   cycle slips through, preserve authored order instead of failing layout. */
                stage.Add(remainingChildIds[0]);
            }

            stages.Add(stage);
            HashSet<RuntimeExecutionTaskId> completedStageIds = stage.ToHashSet();
            remainingChildIds.RemoveAll(completedStageIds.Contains);

            foreach (HashSet<RuntimeExecutionTaskId> dependencySet in remainingDependencies.Values)
            {
                dependencySet.RemoveWhere(completedStageIds.Contains);
            }
        }

        return stages;
    }

    /// <summary>
    /// Maps every visible node inside the provided sibling subtrees back to the direct visible sibling container that
    /// owns that node. Rolled-up descendant dependencies use this to decide whether one sibling group should stage after
    /// another sibling group even when the actual dependency lives on a deeper child node.
    /// </summary>
    private Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> BuildSiblingOwnerMap(IReadOnlyList<RuntimeExecutionTaskId> childIds)
    {
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> ownerMap = new();
        foreach (RuntimeExecutionTaskId childId in childIds)
        {
            foreach (RuntimeExecutionTaskId subtreeNodeId in _projection.EnumerateVisibleSubtreeNodeIds(childId))
            {
                ownerMap[subtreeNodeId] = childId;
            }
        }

        return ownerMap;
    }

    /// <summary>
    /// Lays out one sibling set vertically, treating each child subtree like its own stacked track.
    /// </summary>
    private ExecutionGraphRect LayoutChildrenVertically(IReadOnlyList<RuntimeExecutionTaskId> childIds, double originX, double originY)
    {
        ExecutionGraphRect stackedBounds = ExecutionGraphRect.Empty;
        double currentY = originY;
        foreach (RuntimeExecutionTaskId childId in childIds)
        {
            ExecutionGraphRect childBounds = LayoutNodeSubtree(childId, originX, currentY);
            stackedBounds = stackedBounds.Include(childBounds);
            currentY = childBounds.Bottom + ExecutionGraphLayoutSettings.RowGap;
        }

        return stackedBounds;
    }

    /// <summary>
    /// Lays out dependency stages left-to-right, stacking the tasks within each stage vertically and then centering the
    /// shorter stage columns within the tallest column.
    /// </summary>
    private ExecutionGraphRect LayoutSiblingStages(IReadOnlyList<IReadOnlyList<RuntimeExecutionTaskId>> stages, double originX, double originY)
    {
        ExecutionGraphRect bounds = ExecutionGraphRect.Empty;
        double currentX = originX;
        List<(IReadOnlyList<RuntimeExecutionTaskId> StageIds, ExecutionGraphRect Bounds)> laidOutStages = new(stages.Count);
        foreach (IReadOnlyList<RuntimeExecutionTaskId> stageIds in stages)
        {
            ExecutionGraphRect stageBounds = LayoutChildrenVertically(stageIds, currentX, originY);
            laidOutStages.Add((stageIds, stageBounds));
            bounds = bounds.Include(stageBounds);
            currentX = stageBounds.Right + ExecutionGraphLayoutSettings.ColumnGap;
        }

        return CenterStageColumnsVertically(laidOutStages, bounds);
    }

    /// <summary>
    /// Lays out either a leaf or group subtree for one visible node id.
    /// </summary>
    private ExecutionGraphRect LayoutNodeSubtree(RuntimeExecutionTaskId nodeId, double x, double y)
    {
        return _projection.HasChildren(nodeId)
            ? LayoutGroupNode(nodeId, x, y)
            : LayoutLeafNode(nodeId, x, y);
    }

    /// <summary>
    /// Lays out one leaf task as a normal card.
    /// </summary>
    private ExecutionGraphRect LayoutLeafNode(RuntimeExecutionTaskId nodeId, double x, double y)
    {
        double width = _layoutState.GetMeasuredNodeWidth(nodeId);
        MutableNodeLayout layout = GetNodeLayout(nodeId);
        layout.SetBounds(x, y, width, ExecutionGraphLayoutSettings.NodeHeight, isContainer: false);
        layout.SetHierarchyMetrics(directChildCount: 0, descendantTaskCount: 0, summaryText: string.Empty);
        return layout.Bounds;
    }

    /// <summary>
    /// Lays out one container task as a comment-style frame around its direct visible child nodes.
    /// </summary>
    private ExecutionGraphRect LayoutGroupNode(RuntimeExecutionTaskId nodeId, double x, double y)
    {
        IReadOnlyList<RuntimeExecutionTaskId> childIds = _projection.GetDirectChildIds(nodeId);
        if (childIds.Count == 0)
        {
            double emptyGroupWidth = _layoutState.GetMeasuredNodeWidth(nodeId);
            MutableNodeLayout emptyLayout = GetNodeLayout(nodeId);
            emptyLayout.SetBounds(x, y, emptyGroupWidth, ExecutionGraphLayoutSettings.NodeHeight, isContainer: true);
            emptyLayout.SetHierarchyMetrics(directChildCount: 0, descendantTaskCount: 0, summaryText: "Empty task group");
            return emptyLayout.Bounds;
        }

        LayoutDirectChildren(
            nodeId,
            x + ExecutionGraphLayoutSettings.GroupPadding,
            y + ExecutionGraphLayoutSettings.GroupHeaderHeight + ExecutionGraphLayoutSettings.GroupPadding);

        /* Size the outer frame from the actual laid-out direct-child rectangles instead of a stale intermediate bounds
           accumulator so nested groups use the same child extents that the canvas eventually renders. */
        double maxChildRight = childIds.Max(childId => GetNodeLayout(childId).Bounds.Right);
        double maxChildBottom = childIds.Max(childId => GetNodeLayout(childId).Bounds.Bottom);
        double width = Math.Max(
            _layoutState.GetMeasuredNodeWidth(nodeId),
            Math.Max(ExecutionGraphLayoutSettings.NodeMinWidth, (maxChildRight - x) + ExecutionGraphLayoutSettings.GroupPadding));
        double height = Math.Max(ExecutionGraphLayoutSettings.NodeHeight, (maxChildBottom - y) + ExecutionGraphLayoutSettings.GroupPadding);

        CenterDirectChildrenInGroup(nodeId, x, y, width, height, childIds);

        MutableNodeLayout groupLayout = GetNodeLayout(nodeId);
        groupLayout.SetBounds(x, y, width, height, isContainer: true);
        return groupLayout.Bounds;
    }

    /// <summary>
    /// Centers one already-laid-out direct-child subtree inside the available content area of its parent group while
    /// preserving the internal relative positions of all descendants.
    /// </summary>
    private void CenterDirectChildrenInGroup(
        RuntimeExecutionTaskId groupId,
        double groupX,
        double groupY,
        double groupWidth,
        double groupHeight,
        IReadOnlyList<RuntimeExecutionTaskId> childIds)
    {
        if (childIds.Count == 0)
        {
            return;
        }

        /* The group frame reserves one padded content rectangle beneath the header. Center the direct-child subtree
           inside that rectangle so compact subtrees do not cling to the top-left corner. */
        double minChildX = childIds.Min(childId => GetNodeLayout(childId).Bounds.X);
        double minChildY = childIds.Min(childId => GetNodeLayout(childId).Bounds.Y);
        double maxChildRight = childIds.Max(childId => GetNodeLayout(childId).Bounds.Right);
        double maxChildBottom = childIds.Max(childId => GetNodeLayout(childId).Bounds.Bottom);
        double subtreeWidth = maxChildRight - minChildX;
        double subtreeHeight = maxChildBottom - minChildY;

        double contentX = groupX + ExecutionGraphLayoutSettings.GroupPadding;
        double contentY = groupY + ExecutionGraphLayoutSettings.GroupHeaderHeight + ExecutionGraphLayoutSettings.GroupPadding;
        double contentWidth = Math.Max(0, groupWidth - (ExecutionGraphLayoutSettings.GroupPadding * 2));
        double contentHeight = Math.Max(0, groupHeight - ExecutionGraphLayoutSettings.GroupHeaderHeight - (ExecutionGraphLayoutSettings.GroupPadding * 2));
        double desiredChildX = contentX + Math.Max(0, (contentWidth - subtreeWidth) / 2.0);
        double desiredChildY = contentY + Math.Max(0, (contentHeight - subtreeHeight) / 2.0);
        double deltaX = desiredChildX - minChildX;
        double deltaY = desiredChildY - minChildY;
        if (Math.Abs(deltaX) <= ExecutionGraphLayoutSettings.WidthChangeThreshold &&
            Math.Abs(deltaY) <= ExecutionGraphLayoutSettings.WidthChangeThreshold)
        {
            return;
        }

        foreach (RuntimeExecutionTaskId childId in childIds)
        {
            OffsetSubtree(childId, deltaX, deltaY);
        }
    }

    /// <summary>
    /// Centers each stage column within the tallest sibling-stage column so shorter parallel stacks do not cling to the
    /// top edge when a neighboring stage grows taller.
    /// </summary>
    private ExecutionGraphRect CenterStageColumnsVertically(
        IReadOnlyList<(IReadOnlyList<RuntimeExecutionTaskId> StageIds, ExecutionGraphRect Bounds)> stages,
        ExecutionGraphRect rowBounds)
    {
        if (stages.Count <= 1 || rowBounds.IsEmpty)
        {
            return rowBounds;
        }

        double rowHeight = stages.Max(stage => stage.Bounds.Height);
        bool anyOffsetApplied = false;
        foreach ((IReadOnlyList<RuntimeExecutionTaskId> stageIds, ExecutionGraphRect bounds) in stages)
        {
            double deltaY = (rowHeight - bounds.Height) / 2.0;
            if (Math.Abs(deltaY) <= ExecutionGraphLayoutSettings.WidthChangeThreshold)
            {
                continue;
            }

            foreach (RuntimeExecutionTaskId nodeId in stageIds)
            {
                OffsetSubtree(nodeId, 0, deltaY);
            }

            anyOffsetApplied = true;
        }

        if (!anyOffsetApplied)
        {
            return rowBounds;
        }

        ExecutionGraphRect centeredBounds = ExecutionGraphRect.Empty;
        foreach ((IReadOnlyList<RuntimeExecutionTaskId> stageIds, _) in stages)
        {
            foreach (RuntimeExecutionTaskId nodeId in stageIds)
            {
                centeredBounds = centeredBounds.Include(GetSubtreeBounds(nodeId));
            }
        }

        return centeredBounds;
    }

    /// <summary>
    /// Offsets one node and all of its visible descendants by the same amount.
    /// </summary>
    private void OffsetSubtree(RuntimeExecutionTaskId rootId, double deltaX, double deltaY)
    {
        MutableNodeLayout layout = GetNodeLayout(rootId);
        layout.Offset(deltaX, deltaY);
        foreach (RuntimeExecutionTaskId childId in _projection.GetDirectChildIds(rootId))
        {
            OffsetSubtree(childId, deltaX, deltaY);
        }
    }

    /// <summary>
    /// Returns the bounds of one visible subtree after any centering offsets have been applied.
    /// </summary>
    private ExecutionGraphRect GetSubtreeBounds(RuntimeExecutionTaskId rootId)
    {
        ExecutionGraphRect bounds = GetNodeLayout(rootId).Bounds;
        foreach (RuntimeExecutionTaskId childId in _projection.GetDirectChildIds(rootId))
        {
            bounds = bounds.Include(GetSubtreeBounds(childId));
        }

        return bounds;
    }

    /// <summary>
    /// Populates group summaries after layout so the details pane and container headers can describe their child
    /// composition clearly.
    /// </summary>
    private void ApplyGroupHierarchyMetrics()
    {
        foreach (RuntimeExecutionTaskId nodeId in _projection.VisibleTaskIds.Where(_projection.HasChildren))
        {
            IReadOnlyList<RuntimeExecutionTaskId> directChildIds = _projection.GetDirectChildIds(nodeId);
            IReadOnlyList<RuntimeExecutionTaskId> leafDescendantIds = _projection.GetLeafDescendantIds(nodeId);
            string summaryText = directChildIds.Count == 0
                ? "No child tasks"
                : $"{directChildIds.Count} child item{(directChildIds.Count == 1 ? string.Empty : "s")} - {leafDescendantIds.Count} runnable task{(leafDescendantIds.Count == 1 ? string.Empty : "s")}";
            GetNodeLayout(nodeId).SetHierarchyMetrics(directChildCount: directChildIds.Count, descendantTaskCount: leafDescendantIds.Count, summaryText: summaryText);
        }
    }

    /// <summary>
    /// Builds the visible dependency edges between laid out graph nodes after layout has produced their coordinates.
    /// </summary>
    private List<ExecutionGraphEdgeLayout> BuildDependencyEdges()
    {
        List<ExecutionGraphEdgeLayout> edgeLayouts = new();
        HashSet<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId)> seenEdges = new();
        foreach (RuntimeExecutionTaskId targetId in _projection.VisibleTaskIds)
        {
            foreach (RuntimeExecutionTaskId dependencyId in _projection.GetRawTask(targetId).Dependencies)
            {
                RuntimeExecutionTaskId? visibleDependencyId = _projection.ResolveVisibleTaskId(dependencyId);
                if (visibleDependencyId == null || visibleDependencyId.Value == targetId)
                {
                    continue;
                }

                /* Visible containment already communicates ancestor and descendant structure, so suppress dependency lines
                   inside the same visible branch after hidden-task collapsing remaps internal dependencies. */
                if (_projection.IsVisibleAncestor(visibleDependencyId.Value, targetId) ||
                    _projection.IsVisibleAncestor(targetId, visibleDependencyId.Value))
                {
                    continue;
                }

                (RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId) edgeKey = (visibleDependencyId.Value, targetId);
                if (!seenEdges.Add(edgeKey))
                {
                    continue;
                }

                ExecutionNodeLayout sourceLayout = GetNodeLayout(edgeKey.SourceId).ToImmutable();
                ExecutionNodeLayout targetLayout = GetNodeLayout(edgeKey.TargetId).ToImmutable();
                edgeLayouts.Add(new ExecutionGraphEdgeLayout(edgeKey.SourceId, edgeKey.TargetId, ResolveRoute(sourceLayout, targetLayout)));
            }
        }

        return edgeLayouts;
    }

    /// <summary>
    /// Builds the structural layering snapshot so the canvas can interleave whole edges between unrelated groups.
    /// </summary>
    private ExecutionGraphStructureLayeringSnapshot BuildStructureLayeringSnapshot(IReadOnlyList<ExecutionGraphEdgeLayout> edgeLayouts)
    {
        List<(RuntimeExecutionTaskId GroupId, ExecutionGraphRect Bounds)> orderedGroups;
        List<RuntimeExecutionTaskId> orderedGroupIds;
        /* Group ordering depends only on final container bounds, so cache those bounds once here and reuse them during
           the later crossed-group scan instead of rematerializing the same layouts inside the hot inner loop. */
        using (PerformanceActivityScope orderGroupsActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.BuildStructureLayering.OrderGroups")
            .SetTag("visible.node.count", _projection.VisibleTaskIds.Count))
        {
            orderedGroups = _projection.VisibleTaskIds
                .Select(taskId => (TaskId: taskId, Layout: GetNodeLayout(taskId)))
                .Where(entry => entry.Layout.IsContainer)
                .Select(entry => (GroupId: entry.TaskId, Bounds: entry.Layout.Bounds))
                .OrderByDescending(group => group.Bounds.Width * group.Bounds.Height)
                .ToList();
            orderedGroupIds = orderedGroups.Select(group => group.GroupId).ToList();
            orderGroupsActivity.SetTag("group.count", orderedGroupIds.Count);
        }

        Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), ExecutionGraphEdgeLayeringConstraints> edgeConstraints;
        /* Edge-constraint resolution compares each visible edge against group ancestry and group intersections, so keep
           it separated from the simpler group-order derivation above and split the batch into the three major phases
           that can dominate larger graphs. */
        using (PerformanceActivityScope edgeConstraintsActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.BuildStructureLayering.ResolveEdgeConstraints")
            .SetTag("edge.count", edgeLayouts.Count)
            .SetTag("group.count", orderedGroupIds.Count))
        {
            Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), HashSet<RuntimeExecutionTaskId>> groupsToRenderAboveByEdge = new(edgeLayouts.Count);
            /* Ancestor collection walks the visible parent chain of each endpoint, so batch it separately from the later
               group-intersection scan and depth ordering. */
            using (PerformanceActivityScope collectAboveActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.BuildStructureLayering.ResolveEdgeConstraints.CollectAboveGroups")
                .SetTag("edge.count", edgeLayouts.Count))
            {
                int totalAboveGroupCount = 0;
                foreach (ExecutionGraphEdgeLayout edge in edgeLayouts)
                {
                    (RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId) edgeKey = (edge.SourceId, edge.TargetId);
                    HashSet<RuntimeExecutionTaskId> groupsToRenderAbove = new(_projection.GetVisibleAncestorIds(edge.SourceId));
                    groupsToRenderAbove.UnionWith(_projection.GetVisibleAncestorIds(edge.TargetId));
                    groupsToRenderAboveByEdge[edgeKey] = groupsToRenderAbove;
                    totalAboveGroupCount += groupsToRenderAbove.Count;
                }

                collectAboveActivity.SetTag("total.above.group.count", totalAboveGroupCount);
            }

            Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), List<RuntimeExecutionTaskId>> crossedGroupsByEdge = new(edgeLayouts.Count);
            /* Crossed-group scanning is the broadest part of the algorithm because every edge tests many unrelated group
               bounds, so expose that batch separately from ancestor collection and final list ordering. */
            using (PerformanceActivityScope scanCrossedGroupsActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.BuildStructureLayering.ResolveEdgeConstraints.ScanCrossedGroups")
                .SetTag("edge.count", edgeLayouts.Count)
                .SetTag("group.count", orderedGroupIds.Count))
            {
                int candidateGroupCheckCount = 0;
                int totalBelowGroupCount = 0;
                foreach (ExecutionGraphEdgeLayout edge in edgeLayouts)
                {
                    (RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId) edgeKey = (edge.SourceId, edge.TargetId);
                    HashSet<RuntimeExecutionTaskId> groupsToRenderAbove = groupsToRenderAboveByEdge[edgeKey];
                    ExecutionGraphRect edgeScanBounds = BuildEdgeGroupScanBounds(edge.SourceId, edge.TargetId);
                    List<RuntimeExecutionTaskId> groupsToRenderBelow = new();
                    foreach ((RuntimeExecutionTaskId groupId, ExecutionGraphRect groupBounds) in orderedGroups)
                    {
                        if (groupsToRenderAbove.Contains(groupId))
                        {
                            continue;
                        }

                        candidateGroupCheckCount++;
                        if (DoesEdgeCrossGroup(edgeScanBounds, groupBounds))
                        {
                            groupsToRenderBelow.Add(groupId);
                        }
                    }

                    crossedGroupsByEdge[edgeKey] = groupsToRenderBelow;
                    totalBelowGroupCount += groupsToRenderBelow.Count;
                }

                scanCrossedGroupsActivity.SetTag("candidate.group.check.count", candidateGroupCheckCount)
                    .SetTag("total.below.group.count", totalBelowGroupCount);
            }

            /* Sorting the constraint lists repeatedly queries visible depth, so keep the final ordering work separate from
               the earlier ancestry and intersection scans. */
            using (PerformanceActivityScope orderConstraintListsActivity = CorePerformanceTelemetry.StartActivity("ExecutionGraphLayout.BuildStructureLayering.ResolveEdgeConstraints.OrderConstraintLists")
                .SetTag("edge.count", edgeLayouts.Count))
            {
                int totalOrderedAboveGroupCount = 0;
                int totalOrderedBelowGroupCount = 0;
                edgeConstraints = new Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), ExecutionGraphEdgeLayeringConstraints>(edgeLayouts.Count);
                foreach (ExecutionGraphEdgeLayout edge in edgeLayouts)
                {
                    (RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId) edgeKey = (edge.SourceId, edge.TargetId);
                    List<RuntimeExecutionTaskId> orderedGroupsToRenderAbove = groupsToRenderAboveByEdge[edgeKey]
                        .OrderBy(_projection.GetVisibleDepth)
                        .ToList();
                    List<RuntimeExecutionTaskId> orderedGroupsToRenderBelow = crossedGroupsByEdge[edgeKey]
                        .OrderBy(_projection.GetVisibleDepth)
                        .ToList();
                    totalOrderedAboveGroupCount += orderedGroupsToRenderAbove.Count;
                    totalOrderedBelowGroupCount += orderedGroupsToRenderBelow.Count;
                    edgeConstraints[edgeKey] = new ExecutionGraphEdgeLayeringConstraints(orderedGroupsToRenderAbove, orderedGroupsToRenderBelow);
                }

                orderConstraintListsActivity.SetTag("total.ordered.above.group.count", totalOrderedAboveGroupCount)
                    .SetTag("total.ordered.below.group.count", totalOrderedBelowGroupCount);
            }

            edgeConstraintsActivity.SetTag("edge.constraint.count", edgeConstraints.Count);
        }

        return new ExecutionGraphStructureLayeringSnapshot(orderedGroupIds, edgeConstraints);
    }

    /// <summary>
    /// Builds the conservative edge/group scan rectangle used by whole-edge layering.
    /// </summary>
    private ExecutionGraphRect BuildEdgeGroupScanBounds(RuntimeExecutionTaskId sourceId, RuntimeExecutionTaskId targetId)
    {
        ExecutionGraphRect sourceBounds = GetNodeLayout(sourceId).Bounds;
        ExecutionGraphRect targetBounds = GetNodeLayout(targetId).Bounds;
        return ExecutionGraphRect.FromEdges(
            left: Math.Min(sourceBounds.Right, targetBounds.X),
            top: Math.Min(sourceBounds.Y, targetBounds.Y),
            right: Math.Max(sourceBounds.Right, targetBounds.X),
            bottom: Math.Max(sourceBounds.Bottom, targetBounds.Bottom));
    }

    /// <summary>
    /// Returns whether one cached whole-edge scan rectangle intersects one cached group rectangle at all.
    /// </summary>
    private static bool DoesEdgeCrossGroup(ExecutionGraphRect edgeScanBounds, ExecutionGraphRect groupBounds)
    {
        return edgeScanBounds.Intersects(groupBounds);
    }

    /// <summary>
    /// Chooses the route between two laid out nodes using the existing straight-line and elbow heuristics.
    /// </summary>
    private static ExecutionGraphEdgeRoute ResolveRoute(ExecutionNodeLayout source, ExecutionNodeLayout target)
    {
        SafeSpan sourceHorizontalSpan = GetSafeHorizontalSpan(source);
        SafeSpan targetHorizontalSpan = GetSafeHorizontalSpan(target);
        SafeSpan sourceVerticalSpan = GetSafeVerticalSpan(source);
        SafeSpan targetVerticalSpan = GetSafeVerticalSpan(target);

        RoutedCoordinatePair verticalRoute = ResolveRouteCoordinates(sourceVerticalSpan, targetVerticalSpan);
        if (verticalRoute.IsShared)
        {
            /* When the safe vertical spans overlap, keep the dependency as one straight horizontal segment through the
               shared band so tiny layout nudges do not jump the route onto unrelated node faces. */
            return new ExecutionGraphEdgeRoute(
            [
                new ExecutionGraphPoint(source.X + source.Width, verticalRoute.Source),
                new ExecutionGraphPoint(target.X, verticalRoute.Target)
            ]);
        }

        RoutedCoordinatePair horizontalRoute = ResolveRouteCoordinates(sourceHorizontalSpan, targetHorizontalSpan);
        if (horizontalRoute.IsShared)
        {
            /* When the safe horizontal spans overlap instead, keep the route as one straight vertical segment that touches
               the correct outer faces rather than recentering to a detached midpoint. */
            bool sourceIsAboveTarget = verticalRoute.Source <= verticalRoute.Target;
            return new ExecutionGraphEdgeRoute(
            [
                sourceIsAboveTarget
                    ? new ExecutionGraphPoint(horizontalRoute.Source, source.Y + source.Height)
                    : new ExecutionGraphPoint(horizontalRoute.Source, source.Y),
                sourceIsAboveTarget
                    ? new ExecutionGraphPoint(horizontalRoute.Target, target.Y)
                    : new ExecutionGraphPoint(horizontalRoute.Target, target.Y + target.Height)
            ]);
        }

        /* When neither axis overlaps, connect the nearest safe endpoints picked by the same interval rule used for the
           straight-line cases. */
        ExecutionGraphPoint elbowStart = new(source.X + source.Width, verticalRoute.Source);
        ExecutionGraphPoint elbowEnd = new(target.X, verticalRoute.Target);
        double midpointX = elbowStart.X + Math.Max(ExecutionGraphLayoutSettings.EdgeElbowHorizontalInset, (elbowEnd.X - elbowStart.X) / 2.0);
        return new ExecutionGraphEdgeRoute(
        [
            elbowStart,
            new ExecutionGraphPoint(midpointX, elbowStart.Y),
            new ExecutionGraphPoint(midpointX, elbowEnd.Y),
            elbowEnd
        ]);
    }

    /// <summary>
    /// Returns the safe top edge for routing after applying the rounded-corner buffer.
    /// </summary>
    private static double GetSafeTop(ExecutionNodeLayout node)
    {
        double buffer = GetResolvedCornerBuffer(node);
        return node.Y + buffer;
    }

    /// <summary>
    /// Returns the safe bottom edge for routing after applying the rounded-corner buffer.
    /// </summary>
    private static double GetSafeBottom(ExecutionNodeLayout node)
    {
        double buffer = GetResolvedCornerBuffer(node);
        return node.Y + node.Height - buffer;
    }

    /// <summary>
    /// Returns the safe left edge for routing after applying the rounded-corner buffer.
    /// </summary>
    private static double GetSafeLeft(ExecutionNodeLayout node)
    {
        double buffer = GetResolvedCornerBuffer(node);
        return node.X + buffer;
    }

    /// <summary>
    /// Returns the safe right edge for routing after applying the rounded-corner buffer.
    /// </summary>
    private static double GetSafeRight(ExecutionNodeLayout node)
    {
        double buffer = GetResolvedCornerBuffer(node);
        return node.X + node.Width - buffer;
    }

    /// <summary>
    /// Returns the safe horizontal routing span for one node.
    /// </summary>
    private static SafeSpan GetSafeHorizontalSpan(ExecutionNodeLayout node)
    {
        return new SafeSpan(GetSafeLeft(node), GetSafeRight(node));
    }

    /// <summary>
    /// Returns the safe vertical routing span for one node.
    /// </summary>
    private static SafeSpan GetSafeVerticalSpan(ExecutionNodeLayout node)
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
    /// Resolves the pair of coordinates that should be connected along one axis.
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
    private static double GetResolvedCornerBuffer(ExecutionNodeLayout node)
    {
        return Math.Min(ExecutionGraphLayoutSettings.EdgeCornerBuffer, Math.Max(0.0, (node.Height / 2.0) - 1.0));
    }

    /// <summary>
    /// Returns the mutable layout state for one visible node id.
    /// </summary>
    private MutableNodeLayout GetNodeLayout(RuntimeExecutionTaskId nodeId)
    {
        return _nodeLayouts.TryGetValue(nodeId, out MutableNodeLayout? layout)
            ? layout
            : throw new InvalidOperationException($"No mutable layout exists for visible node '{nodeId}'.");
    }

    /// <summary>
    /// Calculates the total canvas width from the final laid out node bounds.
    /// </summary>
    private static double CalculateCanvasWidth(IEnumerable<ExecutionNodeLayout> nodeLayouts)
    {
        return nodeLayouts.Any()
            ? nodeLayouts.Max(node => node.X + node.Width) + ExecutionGraphLayoutSettings.CanvasMargin
            : ExecutionGraphLayoutSettings.NodeMinWidth;
    }

    /// <summary>
    /// Calculates the total canvas height from the final laid out node bounds.
    /// </summary>
    private static double CalculateCanvasHeight(IEnumerable<ExecutionNodeLayout> nodeLayouts)
    {
        return nodeLayouts.Any()
            ? nodeLayouts.Max(node => node.Y + node.Height) + ExecutionGraphLayoutSettings.CanvasMargin
            : ExecutionGraphLayoutSettings.NodeHeight;
    }

    /// <summary>
    /// Stores one mutable laid out rectangle plus hierarchy metrics before the final immutable snapshot is created.
    /// </summary>
    private sealed class MutableNodeLayout
    {
        private double _x;
        private double _y;
        private double _width = ExecutionGraphLayoutSettings.NodeMinWidth;
        private double _height = ExecutionGraphLayoutSettings.NodeHeight;
        private bool _isContainer;
        private int _directChildCount;
        private int _descendantTaskCount;
        private string _summaryText = string.Empty;

        /// <summary>
        /// Gets the current mutable rectangle.
        /// </summary>
        public ExecutionGraphRect Bounds => new(_x, _y, _width, _height);

        /// <summary>
        /// Gets whether the node currently behaves as a container in the visible hierarchy.
        /// </summary>
        public bool IsContainer => _isContainer;

        /// <summary>
        /// Assigns the current rectangle and container role.
        /// </summary>
        public void SetBounds(double x, double y, double width, double height, bool isContainer)
        {
            _x = x;
            _y = y;
            _width = width;
            _height = height;
            _isContainer = isContainer;
        }

        /// <summary>
        /// Assigns the current hierarchy metrics.
        /// </summary>
        public void SetHierarchyMetrics(int directChildCount, int descendantTaskCount, string summaryText)
        {
            _directChildCount = directChildCount;
            _descendantTaskCount = descendantTaskCount;
            _summaryText = summaryText ?? string.Empty;
        }

        /// <summary>
        /// Offsets the current rectangle while preserving size and metrics.
        /// </summary>
        public void Offset(double deltaX, double deltaY)
        {
            _x += deltaX;
            _y += deltaY;
        }

        /// <summary>
        /// Creates the immutable node-layout snapshot consumed by view models.
        /// </summary>
        public ExecutionNodeLayout ToImmutable()
        {
            return new ExecutionNodeLayout(_x, _y, _width, _height, _isContainer, _directChildCount, _descendantTaskCount, _summaryText);
        }
    }

    /// <summary>
    /// Represents one buffered face span on a single axis so routing can reason about overlap and nearest endpoints.
    /// </summary>
    private readonly struct SafeSpan
    {
        /// <summary>
        /// Creates one normalized safe span.
        /// </summary>
        public SafeSpan(double start, double end)
        {
            Start = Math.Min(start, end);
            End = Math.Max(start, end);
        }

        /// <summary>
        /// Gets the normalized span start.
        /// </summary>
        public double Start { get; }

        /// <summary>
        /// Gets the normalized span end.
        /// </summary>
        public double End { get; }

        /// <summary>
        /// Gets the center point of the span.
        /// </summary>
        public double Center => Start + ((End - Start) / 2.0);

        /// <summary>
        /// Returns whether this span overlaps another span.
        /// </summary>
        public bool Overlaps(SafeSpan other)
        {
            return Start <= other.End && other.Start <= End;
        }
    }

    /// <summary>
    /// Carries the source and target coordinates chosen for one routing axis.
    /// </summary>
    private readonly struct RoutedCoordinatePair
    {
        /// <summary>
        /// Creates one routed coordinate pair.
        /// </summary>
        public RoutedCoordinatePair(double source, double target)
        {
            Source = source;
            Target = target;
        }

        /// <summary>
        /// Gets the source coordinate.
        /// </summary>
        public double Source { get; }

        /// <summary>
        /// Gets the target coordinate.
        /// </summary>
        public double Target { get; }

        /// <summary>
        /// Gets whether both ends share the same resolved coordinate.
        /// </summary>
        public bool IsShared => Math.Abs(Source - Target) <= 0.001;
    }
}
