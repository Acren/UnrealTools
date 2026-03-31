using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using LocalAutomation.Avalonia.Collections;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts a shared execution plan into Avalonia graph nodes, comment-style group containers, dependency edges,
/// selection state, and layout details for the runtime workspace.
/// </summary>
public sealed class ExecutionGraphViewModel : ViewModelBase
{
    /// <summary>
    /// Defines the minimum width used for leaf task cards so short titles still read as graph nodes rather than badges.
    /// </summary>
    public const double NodeMinWidth = 156;

    /// <summary>
    /// Defines the maximum width used for leaf task cards so content-driven sizing does not create ragged oversized columns.
    /// </summary>
    public const double NodeMaxWidth = 320;

    /// <summary>
    /// Defines the fixed height used for leaf task cards.
    /// </summary>
    public const double NodeHeight = 84;

    /// <summary>
    /// Defines the fixed height reserved for a group-container header.
    /// </summary>
    public const double GroupHeaderHeight = 48;

    /// <summary>
    /// Defines the padding between a group-container border and its child items.
    /// </summary>
    public const double GroupPadding = 18;

    /// <summary>
    /// Matches the task-card inner padding so content-driven width calculations align with the rendered card chrome.
    /// </summary>
    private const double NodeHorizontalPadding = 48;

    /// <summary>
    /// Matches the title text size used by the execution task card so width measurement stays consistent with the rendered UI.
    /// </summary>
    private const double NodeTitleFontSize = 14;

    /// <summary>
    /// Matches the status-label font size used by the execution task card so status rows contribute the correct width.
    /// </summary>
    private const double NodeStatusFontSize = 11;

    /// <summary>
    /// Reserves space for the status dot and gap so content-driven width sizing reflects the full status row footprint.
    /// </summary>
    private const double NodeStatusAdornmentWidth = 20;

    /* Column spacing stays generous enough for status glows and dependency elbows even when task cards shrink toward
       their content width. */
    private const double ColumnGap = 84;
    private const double RowGap = 30;
    private static readonly Typeface GraphNodeTypeface = new(new FontFamily("avares://LocalAutomation.Avalonia/Assets/Fonts#Inter"));
    // The graph keeps one fixed pseudo-node for merged output selection that never participates in runtime plan matching.
    private static readonly ExecutionTaskId AllOutputNodeId = new("all-output");

    private readonly RangeObservableCollection<ExecutionNodeViewModel> _nodes = new();
    private readonly RangeObservableCollection<ExecutionEdgeViewModel> _edges = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionNodeViewModel> _nodesById = new();
    private readonly Dictionary<ExecutionTaskId, List<ExecutionTaskId>> _childrenByParentId = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionTaskId?> _parentByTaskId = new();
    private readonly Dictionary<ExecutionTaskId, List<ExecutionTaskId>> _leafDescendantsByGroupId = new();
    private ExecutionPlan? _plan;
    private ExecutionNodeViewModel? _selectedNode;
    private double _canvasWidth;
    private double _canvasHeight;
    private bool _isUpdatingGraph;

    // The root lookup uses a non-runnable sentinel so root-level tasks can share the same typed dictionary shape as nested groups.
    private static readonly ExecutionTaskId RootParentId = new("root-parent");

    /// <summary>
    /// Creates a graph view model with the fixed all-output pseudo-node available for task-log selection.
    /// </summary>
    public ExecutionGraphViewModel()
    {
        AllOutputNode = new ExecutionNodeViewModel(new ExecutionTask(
            id: AllOutputNodeId,
            title: "All Output",
            description: "Show the merged output stream for the current preview or execution session.",
            status: ExecutionTaskStatus.Pending));
    }

    /// <summary>
    /// Gets the current execution plan rendered by the graph.
    /// </summary>
    public ExecutionPlan? Plan
    {
        get => _plan;
        private set => SetProperty(ref _plan, value);
    }

    /// <summary>
    /// Gets the rendered graph-node collection.
    /// </summary>
    public ObservableCollection<ExecutionNodeViewModel> Nodes => _nodes;

    /// <summary>
    /// Gets the rendered graph-edge collection.
    /// </summary>
    public ObservableCollection<ExecutionEdgeViewModel> Edges => _edges;

    /// <summary>
    /// Gets whether the graph is currently rebuilding its internal collections and bounds as one bulk update.
    /// </summary>
    public bool IsUpdatingGraph
    {
        get => _isUpdatingGraph;
        private set => SetProperty(ref _isUpdatingGraph, value);
    }

    /// <summary>
    /// Gets the special pseudo-node that selects the merged output stream.
    /// </summary>
    public ExecutionNodeViewModel AllOutputNode { get; }

    /// <summary>
    /// Gets the currently selected graph node for details and task-log binding.
    /// </summary>
    public ExecutionNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        private set
        {
            if (_selectedNode == value)
            {
                return;
            }

            if (_selectedNode != null)
            {
                _selectedNode.IsSelected = false;
            }

            _selectedNode = value;
            if (_selectedNode != null)
            {
                _selectedNode.IsSelected = true;
            }

            RaisePropertyChanged(nameof(SelectedNode));
            RaisePropertyChanged(nameof(SelectedNodeTitle));
            RaisePropertyChanged(nameof(SelectedNodeDescription));
            RaisePropertyChanged(nameof(SelectedNodeStatusText));
            RaisePropertyChanged(nameof(SelectedNodeStatusReason));
            RaisePropertyChanged(nameof(SelectedTaskId));
            RaisePropertyChanged(nameof(HasSelectedNode));
            RaisePropertyChanged(nameof(IsAllOutputSelected));
        }
    }

    /// <summary>
    /// Gets whether the graph currently has a selected graph node.
    /// </summary>
    public bool HasSelectedNode => SelectedNode != null;

    /// <summary>
    /// Gets whether the all-output pseudo-node is selected.
    /// </summary>
    public bool IsAllOutputSelected => SelectedNode?.Id == AllOutputNodeId;

    /// <summary>
    /// Gets the title for the selected graph node details pane.
    /// </summary>
    public string SelectedNodeTitle => SelectedNode?.Title ?? "No graph node selected";

    /// <summary>
    /// Gets the description for the selected graph node details pane.
    /// </summary>
    public string SelectedNodeDescription => SelectedNode?.DetailsText ?? "Select a graph node to inspect the underlying task details and output.";

    /// <summary>
    /// Gets the selected task id for details and status binding. Only the synthetic all-output node maps to no task.
    /// </summary>
    public ExecutionTaskId? SelectedTaskId => SelectedNode == null || IsAllOutputSelected ? null : SelectedNode.Id;

    /// <summary>
    /// Returns the selected task id plus all descendant task ids so the UI can show one hierarchical task subtree
    /// without treating parent tasks specially at runtime.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> GetSelectedLogTaskIds()
    {
        if (SelectedNode == null || IsAllOutputSelected)
        {
            return Array.Empty<ExecutionTaskId>();
        }

        return GetTaskSubtreeIds(SelectedNode.Id).ToList();
    }

    /// <summary>
    /// Gets the status label for the selected graph node.
    /// </summary>
    public string SelectedNodeStatusText => SelectedNode?.StatusText ?? string.Empty;

    /// <summary>
    /// Gets the status reason for the selected graph node.
    /// </summary>
    public string SelectedNodeStatusReason => SelectedNode?.StatusReason ?? string.Empty;

    /// <summary>
    /// Gets the total canvas width required to render the current graph.
    /// </summary>
    public double CanvasWidth
    {
        get => _canvasWidth;
        private set => SetProperty(ref _canvasWidth, value);
    }

    /// <summary>
    /// Gets the total canvas height required to render the current graph.
    /// </summary>
    public double CanvasHeight
    {
        get => _canvasHeight;
        private set => SetProperty(ref _canvasHeight, value);
    }

    /// <summary>
    /// Replaces the rendered graph with the provided plan and recomputes the nested comment-container layout.
    /// </summary>
    public void SetPlan(ExecutionPlan? plan)
    {
        /* Trace plan-graph rebuild phases separately so option-edit latency can be attributed to lookup creation, layout,
           metrics, edge construction, or selection restoration rather than treating graph refresh as one opaque cost. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraph.SetPlan");
        PerformanceTelemetry.SetTag(activity, "plan.has_result", plan != null);
        PerformanceTelemetry.SetTag(activity, "plan.task.count", plan?.Tasks.Count ?? 0);

        IsUpdatingGraph = true;
        try
        {
            Plan = plan;
            _nodes.Clear();
            _edges.Clear();
            _nodesById.Clear();
            _childrenByParentId.Clear();
            _parentByTaskId.Clear();
            _leafDescendantsByGroupId.Clear();

            if (plan == null)
            {
                CanvasWidth = NodeMinWidth;
                CanvasHeight = NodeHeight;
                SelectNode(AllOutputNode);
                return;
            }

            using (PerformanceActivityScope buildNodesActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.BuildNodeLookup"))
            {
                PerformanceTelemetry.SetTag(buildNodesActivity, "plan.task.count", plan.Tasks.Count);
                BuildNodeLookup(plan);
            }

            using (PerformanceActivityScope buildHierarchyActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.BuildHierarchyLookups"))
            {
                BuildHierarchyLookups(plan.Tasks);
            }

            using (PerformanceActivityScope layoutActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.Layout"))
            {
                LayoutDirectChildren(parentId: null, originX: 24, originY: 24);
            }

            using (PerformanceActivityScope metricsActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.ApplyHierarchyMetrics"))
            {
                ApplyGroupHierarchyMetrics();
                ApplyGroupRollupStatuses();
            }

            using (PerformanceActivityScope edgesActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.BuildDependencyEdges"))
            {
                BuildDependencyEdges(plan);
                PerformanceTelemetry.SetTag(edgesActivity, "edge.count", _edges.Count);
            }

            using (PerformanceActivityScope canvasActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.UpdateCanvasSize"))
            {
                UpdateCanvasSize();
                PerformanceTelemetry.SetTag(canvasActivity, "canvas.width", CanvasWidth);
                PerformanceTelemetry.SetTag(canvasActivity, "canvas.height", CanvasHeight);
            }

            using (PerformanceActivityScope selectionActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.RestoreSelection"))
            {
                RestoreSelection();
            }
        }
        finally
        {
            IsUpdatingGraph = false;
        }
    }

    /// <summary>
    /// Selects one graph node in the rendered plan.
    /// </summary>
    public void SelectNode(ExecutionNodeViewModel? node)
    {
        SelectedNode = node ?? AllOutputNode;
    }

    /// <summary>
    /// Updates one rendered task's runtime status from session events and refreshes any parent group summaries.
    /// </summary>
    public void UpdateTaskStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason)
    {
        if (!_nodesById.TryGetValue(taskId, out ExecutionNodeViewModel? node))
        {
            return;
        }

        node.SetStatus(status, statusReason);
        RefreshAncestorGroupStatuses(taskId);

        if (ReferenceEquals(SelectedNode, node) || (SelectedNode != null && SelectedNode.IsContainer))
        {
            RaisePropertyChanged(nameof(SelectedNodeStatusText));
            RaisePropertyChanged(nameof(SelectedNodeStatusReason));
            RaisePropertyChanged(nameof(SelectedNodeDescription));
        }
    }

    /// <summary>
    /// Materializes one graph node view model for every task in the source plan.
    /// </summary>
    private void BuildNodeLookup(ExecutionPlan plan)
    {
        List<ExecutionNodeViewModel> nodes = new(plan.Tasks.Count);
        foreach (ExecutionTask task in plan.Tasks)
        {
            ExecutionNodeViewModel node = new(task);
            nodes.Add(node);
            _nodesById[task.Id] = node;
        }

        _nodes.AddRange(nodes);
    }

    /// <summary>
    /// Builds the parent and child lookup tables that drive nested group-container layout.
    /// </summary>
    private void BuildHierarchyLookups(IEnumerable<ExecutionTask> tasks)
    {
        foreach (ExecutionTask task in tasks)
        {
            _parentByTaskId[task.Id] = task.ParentId;
            ExecutionTaskId parentId = task.ParentId ?? RootParentId;
            if (!_childrenByParentId.TryGetValue(parentId, out List<ExecutionTaskId>? childIds))
            {
                childIds = new List<ExecutionTaskId>();
                _childrenByParentId[parentId] = childIds;
            }

            childIds.Add(task.Id);
        }
    }

    /// <summary>
    /// Lays out one sibling set using a local dependency-depth arrangement so grouped tasks remain readable inside their
    /// containers.
    /// </summary>
    private LayoutBounds LayoutDirectChildren(ExecutionTaskId? parentId, double originX, double originY)
    {
        IReadOnlyList<ExecutionNodeViewModel> children = GetDirectChildren(parentId);
        if (children.Count == 0)
        {
            return LayoutBounds.Empty;
        }

        /* Parent groups read more clearly when their child groups stack in authored order rather than fanning out into
           dependency columns. This keeps high-level branches like per-engine deploy groups easy to scan while leaf-task
           siblings still use the compact DAG layout below. */
        if (children.All(child => HasChildren(child.Id)))
        {
            LayoutBounds stackedBounds = LayoutBounds.Empty;
            double currentY = originY;

            foreach (ExecutionNodeViewModel child in children)
            {
                LayoutBounds childBounds = LayoutGroupNode(child, originX, currentY);
                stackedBounds = stackedBounds.Include(childBounds);
                currentY = childBounds.Bottom + RowGap;
            }

            return stackedBounds;
        }

        Dictionary<ExecutionTaskId, int> localDepths = ComputeSiblingDepths(children);
        Dictionary<int, List<ExecutionNodeViewModel>> columns = children
            .GroupBy(child => localDepths.TryGetValue(child.Id, out int depth) ? depth : 0)
            .OrderBy(group => group.Key)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(child => HasChildren(child.Id) ? 0 : 1)
                    .ThenBy(child => child.Title, StringComparer.Ordinal)
                    .ToList());

        LayoutBounds bounds = LayoutBounds.Empty;
        double currentColumnX = originX;

        foreach ((_, List<ExecutionNodeViewModel> columnNodes) in columns)
        {
            /* Container columns cannot be sized from placeholder node widths because group bounds are only known after
               laying out their descendants. Lay out each column in order, measure its real bounds, then place the next
               column after that actual width. */
            double currentY = originY;
            LayoutBounds columnBounds = LayoutBounds.Empty;

            foreach (ExecutionNodeViewModel node in columnNodes)
            {
                LayoutBounds nodeBounds = HasChildren(node.Id)
                    ? LayoutGroupNode(node, currentColumnX, currentY)
                    : LayoutLeafNode(node, currentColumnX, currentY);

                bounds = bounds.Include(nodeBounds);
                columnBounds = columnBounds.Include(nodeBounds);
                currentY = nodeBounds.Bottom + RowGap;
            }

            currentColumnX = columnBounds.Right + ColumnGap;
        }

        return bounds;
    }

    /// <summary>
    /// Lays out one leaf task as a normal card.
    /// </summary>
    private LayoutBounds LayoutLeafNode(ExecutionNodeViewModel node, double x, double y)
    {
        double width = GetLeafNodeWidth(node);
        node.SetBounds(x, y, width, NodeHeight);
        node.SetHierarchyMetrics(directChildCount: 0, descendantTaskCount: 0, summaryText: string.Empty);
        return new LayoutBounds(x, y, width, NodeHeight);
    }

    /// <summary>
    /// Lays out one container task as a Blueprint-comment-style frame around its direct child nodes.
    /// </summary>
    private LayoutBounds LayoutGroupNode(ExecutionNodeViewModel node, double x, double y)
    {
        IReadOnlyList<ExecutionNodeViewModel> children = GetDirectChildren(node.Id);
        if (children.Count == 0)
        {
            node.SetBounds(x, y, NodeMinWidth, NodeHeight);
            node.SetHierarchyMetrics(directChildCount: 0, descendantTaskCount: 0, summaryText: "Empty task group");
            return new LayoutBounds(x, y, NodeMinWidth, NodeHeight);
        }

        LayoutBounds childBounds = LayoutDirectChildren(node.Id, x + GroupPadding, y + GroupHeaderHeight + GroupPadding);
        double width = Math.Max(NodeMinWidth, childBounds.Width + (GroupPadding * 2));
        double height = Math.Max(NodeHeight, GroupHeaderHeight + childBounds.Height + (GroupPadding * 2));
        node.SetBounds(x, y, width, height);
        return new LayoutBounds(x, y, width, height);
    }

    /// <summary>
    /// Computes dependency depths only within one sibling set so local group contents can lay out like a compact DAG.
    /// </summary>
    private Dictionary<ExecutionTaskId, int> ComputeSiblingDepths(IReadOnlyList<ExecutionNodeViewModel> siblings)
    {
        if (Plan == null)
        {
            return new Dictionary<ExecutionTaskId, int>();
        }

        HashSet<ExecutionTaskId> siblingIds = new(siblings.Select(sibling => sibling.Id));
        Dictionary<ExecutionTaskId, int> depths = new();

        int ComputeDepth(ExecutionTaskId taskId)
        {
            if (depths.TryGetValue(taskId, out int existingDepth))
            {
                return existingDepth;
            }

            IReadOnlyList<ExecutionTaskId> dependencies = Plan
                .GetTaskDependencies(taskId)
                .Where(siblingIds.Contains)
                .ToList();
            int depth = dependencies.Count == 0 ? 0 : dependencies.Max(ComputeDepth) + 1;
            depths[taskId] = depth;
            return depth;
        }

        foreach (ExecutionNodeViewModel sibling in siblings)
        {
            ComputeDepth(sibling.Id);
        }

        return depths;
    }

    /// <summary>
    /// Populates the group summaries after layout so the details pane and container headers can describe their child
    /// composition clearly.
    /// </summary>
    private void ApplyGroupHierarchyMetrics()
    {
        /* Container styling and rendering must be derived from the structural hierarchy rather than the derived
           IsContainer flag, because DirectChildCount is assigned by this pass. If we filter on IsContainer here, no
           parent task is ever initialized as a container after the task-kind cleanup. */
        foreach (ExecutionNodeViewModel node in _nodes.Where(node => HasChildren(node.Id)))
        {
            IReadOnlyList<ExecutionNodeViewModel> directChildren = GetDirectChildren(node.Id);
            List<ExecutionTaskId> leafDescendants = GetLeafDescendantIds(node.Id).ToList();
            _leafDescendantsByGroupId[node.Id] = leafDescendants;

            string summaryText = directChildren.Count == 0
                ? "No child tasks"
                : $"{directChildren.Count} child item{(directChildren.Count == 1 ? string.Empty : "s")} · {leafDescendants.Count} runnable task{(leafDescendants.Count == 1 ? string.Empty : "s")}";

            node.SetHierarchyMetrics(directChildren.Count, leafDescendants.Count, summaryText);
        }
    }

    /// <summary>
    /// Preserves group hierarchy metrics without overriding real task statuses, because parent tasks remain first-class
    /// runtime tasks rather than derived summary containers.
    /// </summary>
    private void ApplyGroupRollupStatuses()
    {
        return;
    }

    /// <summary>
    /// Intentionally preserves each group's own status. The graph shows child statuses directly in the hierarchy, so no
    /// derived rollup badge is needed.
    /// </summary>
    private void ApplyGroupRollupStatus(ExecutionNodeViewModel group)
    {
        _ = group;
    }

    /// <summary>
    /// Recomputes every ancestor group of the updated task so nested containers stay in sync with live execution.
    /// </summary>
    private void RefreshAncestorGroupStatuses(ExecutionTaskId taskId)
    {
        ExecutionTaskId? currentParentId = _parentByTaskId.TryGetValue(taskId, out ExecutionTaskId? parentId) ? parentId : null;
        while (currentParentId is ExecutionTaskId parent && _nodesById.TryGetValue(parent, out ExecutionNodeViewModel? group))
        {
            ApplyGroupRollupStatus(group);
            currentParentId = _parentByTaskId.TryGetValue(parent, out ExecutionTaskId? nextParentId) ? nextParentId : null;
        }
    }

    /// <summary>
    /// Builds the visible dependency edges between runnable leaf nodes after layout has produced their coordinates.
    /// </summary>
    private void BuildDependencyEdges(ExecutionPlan plan)
    {
        List<ExecutionEdgeViewModel> edges = new();
        foreach (ExecutionDependency dependency in plan.Dependencies)
        {
            if (_nodesById.TryGetValue(dependency.SourceTaskId, out ExecutionNodeViewModel? source) &&
                _nodesById.TryGetValue(dependency.TargetTaskId, out ExecutionNodeViewModel? target) &&
                !HasChildren(source.Id) &&
                !HasChildren(target.Id))
            {
                edges.Add(new ExecutionEdgeViewModel(source, target));
            }
        }

        _edges.AddRange(edges);
    }

    /// <summary>
    /// Updates the canvas size from the laid out node bounds.
    /// </summary>
    private void UpdateCanvasSize()
    {
        if (_nodes.Count == 0)
        {
            CanvasWidth = NodeMinWidth;
            CanvasHeight = NodeHeight;
            return;
        }

        CanvasWidth = _nodes.Max(node => node.X + node.Width) + 24;
        CanvasHeight = _nodes.Max(node => node.Y + node.Height) + 24;
    }

    /// <summary>
    /// Restores the default graph selection after the plan changes. The runtime workspace deliberately starts on merged
    /// output so the log pane is useful immediately without forcing a task click.
    /// </summary>
    private void RestoreSelection()
    {
        SelectNode(AllOutputNode);
    }

    /// <summary>
    /// Returns the direct child graph nodes for the provided parent task id.
    /// </summary>
    private IReadOnlyList<ExecutionNodeViewModel> GetDirectChildren(ExecutionTaskId? parentId)
    {
        ExecutionTaskId lookupParentId = parentId ?? RootParentId;
        if (!_childrenByParentId.TryGetValue(lookupParentId, out List<ExecutionTaskId>? childIds))
        {
            return Array.Empty<ExecutionNodeViewModel>();
        }

        return childIds.Select(childId => _nodesById[childId]).ToList();
    }

    /// <summary>
    /// Enumerates all descendant runnable task ids beneath the provided container task id.
    /// </summary>
    private IEnumerable<ExecutionTaskId> GetLeafDescendantIds(ExecutionTaskId groupId)
    {
        if (!_childrenByParentId.TryGetValue(groupId, out List<ExecutionTaskId>? childIds))
        {
            yield break;
        }

        foreach (ExecutionTaskId childId in childIds)
        {
            ExecutionNodeViewModel child = _nodesById[childId];
            if (!HasChildren(childId))
            {
                yield return childId;
                continue;
            }

            foreach (ExecutionTaskId descendantId in GetLeafDescendantIds(childId))
            {
                yield return descendantId;
            }
        }
    }

    /// <summary>
    /// Returns whether the provided task currently has direct child tasks in the hierarchy projection.
    /// Layout must use
    /// this lookup directly because container metrics are populated after node bounds are assigned.
    /// </summary>
    private bool HasChildren(ExecutionTaskId taskId)
    {
        return _childrenByParentId.TryGetValue(taskId, out List<ExecutionTaskId>? childIds) && childIds.Count > 0;
    }

    /// <summary>
     /// Sizes one leaf card from the larger of its title row and status row, then clamps the result so the graph stays
     /// compact while matching the rendered card padding.
     /// </summary>
    private static double GetLeafNodeWidth(ExecutionNodeViewModel node)
    {
        double titleWidth = MeasureSingleLineText(node.Title, NodeTitleFontSize);
        double statusWidth = MeasureSingleLineText(node.StatusLabelText, NodeStatusFontSize) + NodeStatusAdornmentWidth;
        double contentWidth = Math.Max(titleWidth, statusWidth);
        return Math.Clamp(Math.Ceiling(contentWidth + NodeHorizontalPadding), NodeMinWidth, NodeMaxWidth);
    }

    /// <summary>
    /// Measures one short single-line text fragment with the same typeface used by the execution graph so layout width
    /// stays close to the rendered card content width.
    /// </summary>
    private static double MeasureSingleLineText(string text, double fontSize)
    {
        FormattedText measuredText = new(
            text ?? string.Empty,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            GraphNodeTypeface,
            fontSize,
            Brushes.White);
        return measuredText.WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// Enumerates one task subtree including the selected task itself and every descendant task so log selection can be
    /// hierarchical without mutating runtime task ownership.
    /// </summary>
    private IEnumerable<ExecutionTaskId> GetTaskSubtreeIds(ExecutionTaskId rootId)
    {
        yield return rootId;

        if (!_childrenByParentId.TryGetValue(rootId, out List<ExecutionTaskId>? childIds))
        {
            yield break;
        }

        foreach (ExecutionTaskId childId in childIds)
        {
            foreach (ExecutionTaskId descendantId in GetTaskSubtreeIds(childId))
            {
                yield return descendantId;
            }
        }
    }

    /// <summary>
    /// Stores one laid out rectangle without pulling additional geometry dependencies into the view-model layer.
    /// </summary>
    private readonly struct LayoutBounds
    {
        public static LayoutBounds Empty { get; } = new(0, 0, 0, 0, isEmpty: true);

        private LayoutBounds(double x, double y, double width, double height, bool isEmpty = false)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            IsEmpty = isEmpty;
        }

        public LayoutBounds(double x, double y, double width, double height)
            : this(x, y, width, height, isEmpty: false)
        {
        }

        public double X { get; }

        public double Y { get; }

        public double Width { get; }

        public double Height { get; }

        public bool IsEmpty { get; }

        public double Right => X + Width;

        public double Bottom => Y + Height;

        public LayoutBounds Include(LayoutBounds other)
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
            return new LayoutBounds(left, top, right - left, bottom - top);
        }
    }
}
