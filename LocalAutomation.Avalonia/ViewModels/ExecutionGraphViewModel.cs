using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts a shared execution plan into Avalonia graph nodes, comment-style group containers, dependency edges,
/// selection state, and layout details for the runtime workspace.
/// </summary>
public sealed class ExecutionGraphViewModel : ViewModelBase
{
    /// <summary>
    /// Defines the fixed width used for leaf task cards.
    /// </summary>
    public const double NodeWidth = 220;

    /// <summary>
    /// Defines the fixed height used for leaf task cards.
    /// </summary>
    public const double NodeHeight = 92;

    /// <summary>
    /// Defines the fixed height reserved for a group-container header.
    /// </summary>
    public const double GroupHeaderHeight = 42;

    /// <summary>
    /// Defines the padding between a group-container border and its child items.
    /// </summary>
    public const double GroupPadding = 18;

    private const double ColumnGap = 110;
    private const double RowGap = 36;
    // The graph keeps one fixed pseudo-node for merged output selection that never participates in runtime plan matching.
    private static readonly ExecutionTaskId AllOutputNodeId = new("all-output");

    private readonly ObservableCollection<ExecutionNodeViewModel> _nodes = new();
    private readonly ObservableCollection<ExecutionEdgeViewModel> _edges = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionNodeViewModel> _nodesById = new();
    private readonly Dictionary<ExecutionTaskId, List<ExecutionTaskId>> _childrenByParentId = new();
    private readonly Dictionary<ExecutionTaskId, ExecutionTaskId?> _parentByTaskId = new();
    private readonly Dictionary<ExecutionTaskId, List<ExecutionTaskId>> _leafDescendantsByGroupId = new();
    private ExecutionPlan? _plan;
    private ExecutionNodeViewModel? _selectedNode;
    private double _canvasWidth;
    private double _canvasHeight;

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
            kind: ExecutionTaskKind.Group,
            status: ExecutionTaskStatus.Ready));
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
        Plan = plan;
        _nodes.Clear();
        _edges.Clear();
        _nodesById.Clear();
        _childrenByParentId.Clear();
        _parentByTaskId.Clear();
        _leafDescendantsByGroupId.Clear();

        if (plan == null)
        {
            CanvasWidth = NodeWidth;
            CanvasHeight = NodeHeight;
            SelectNode(AllOutputNode);
            return;
        }

        BuildNodeLookup(plan);
        BuildHierarchyLookups(plan.Tasks);
        LayoutDirectChildren(parentId: null, originX: 24, originY: 24);
        ApplyGroupHierarchyMetrics();
        ApplyGroupRollupStatuses();
        BuildDependencyEdges(plan);
        UpdateCanvasSize();
        RestoreSelection();
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

        if (ReferenceEquals(SelectedNode, node) || (SelectedNode != null && SelectedNode.IsGroup))
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
        foreach (ExecutionTask task in plan.Tasks)
        {
            ExecutionNodeViewModel node = new(task);
            _nodes.Add(node);
            _nodesById[task.Id] = node;
        }
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

        Dictionary<ExecutionTaskId, int> localDepths = ComputeSiblingDepths(children);
        Dictionary<int, List<ExecutionNodeViewModel>> columns = children
            .GroupBy(child => localDepths.TryGetValue(child.Id, out int depth) ? depth : 0)
            .OrderBy(group => group.Key)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(child => child.IsGroup ? 0 : 1)
                    .ThenBy(child => child.Title, StringComparer.Ordinal)
                    .ToList());

        LayoutBounds bounds = LayoutBounds.Empty;
        foreach ((int depth, List<ExecutionNodeViewModel> columnNodes) in columns)
        {
            double columnX = originX + (depth * (NodeWidth + ColumnGap));
            double currentY = originY;

            foreach (ExecutionNodeViewModel node in columnNodes)
            {
                LayoutBounds nodeBounds = node.IsGroup
                    ? LayoutGroupNode(node, columnX, currentY)
                    : LayoutLeafNode(node, columnX, currentY);

                bounds = bounds.Include(nodeBounds);
                currentY = nodeBounds.Bottom + RowGap;
            }
        }

        return bounds;
    }

    /// <summary>
    /// Lays out one non-group task as a normal card.
    /// </summary>
    private static LayoutBounds LayoutLeafNode(ExecutionNodeViewModel node, double x, double y)
    {
        node.SetBounds(x, y, NodeWidth, NodeHeight);
        node.SetHierarchyMetrics(directChildCount: 0, descendantTaskCount: 0, summaryText: string.Empty);
        return new LayoutBounds(x, y, NodeWidth, NodeHeight);
    }

    /// <summary>
    /// Lays out one group task as a Blueprint-comment-style container around its direct child nodes.
    /// </summary>
    private LayoutBounds LayoutGroupNode(ExecutionNodeViewModel node, double x, double y)
    {
        IReadOnlyList<ExecutionNodeViewModel> children = GetDirectChildren(node.Id);
        if (children.Count == 0)
        {
            node.SetBounds(x, y, NodeWidth, NodeHeight);
            node.SetHierarchyMetrics(directChildCount: 0, descendantTaskCount: 0, summaryText: "Empty group");
            return new LayoutBounds(x, y, NodeWidth, NodeHeight);
        }

        LayoutBounds childBounds = LayoutDirectChildren(node.Id, x + GroupPadding, y + GroupHeaderHeight + GroupPadding);
        double width = Math.Max(NodeWidth, childBounds.Width + (GroupPadding * 2));
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
        foreach (ExecutionNodeViewModel node in _nodes.Where(node => node.IsGroup))
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
        foreach (ExecutionDependency dependency in plan.Dependencies)
        {
            if (_nodesById.TryGetValue(dependency.SourceTaskId, out ExecutionNodeViewModel? source) &&
                _nodesById.TryGetValue(dependency.TargetTaskId, out ExecutionNodeViewModel? target) &&
                !source.IsGroup &&
                !target.IsGroup)
            {
                _edges.Add(new ExecutionEdgeViewModel(source, target));
            }
        }
    }

    /// <summary>
    /// Updates the canvas size from the laid out node bounds.
    /// </summary>
    private void UpdateCanvasSize()
    {
        if (_nodes.Count == 0)
        {
            CanvasWidth = NodeWidth;
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
    /// Enumerates all descendant runnable task ids beneath the provided group id.
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
            if (!child.IsGroup)
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
