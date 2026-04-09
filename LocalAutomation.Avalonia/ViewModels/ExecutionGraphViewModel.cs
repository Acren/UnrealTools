using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using LocalAutomation.Avalonia.Collections;
using LocalAutomation.Core;
using RuntimeExecutionTask = LocalAutomation.Runtime.ExecutionTask;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts a shared execution plan into Avalonia graph nodes, comment-style group containers, dependency edges,
/// selection state, and layout details for the runtime workspace.
/// </summary>
public sealed class ExecutionGraphViewModel : ViewModelBase
{
    /// <summary>
    /// Carries the visible group ordering plus whole-edge slot constraints for one graph snapshot so the canvas can apply
    /// interleaved Z ordering without teaching every edge view model about group ancestry.
    /// </summary>
    public sealed class StructureLayeringSnapshot
    {
        /// <summary>
        /// Creates one immutable layering snapshot from the current visible groups and per-edge constraints.
        /// </summary>
        public StructureLayeringSnapshot(
            IReadOnlyList<ExecutionNodeViewModel> orderedGroups,
            IReadOnlyDictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), EdgeLayeringConstraints> edgeConstraints)
        {
            OrderedGroups = orderedGroups ?? throw new ArgumentNullException(nameof(orderedGroups));
            EdgeConstraints = edgeConstraints ?? throw new ArgumentNullException(nameof(edgeConstraints));
        }

        /// <summary>
        /// Gets visible groups in deterministic base draw order from back to front.
        /// </summary>
        public IReadOnlyList<ExecutionNodeViewModel> OrderedGroups { get; }

        /// <summary>
        /// Gets the whole-edge ordering constraints keyed by visible source and target identifiers.
        /// </summary>
        public IReadOnlyDictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), EdgeLayeringConstraints> EdgeConstraints { get; }
    }

    /// <summary>
    /// Carries the groups a whole edge must render above and below for one graph snapshot.
    /// </summary>
    public sealed class EdgeLayeringConstraints
    {
        /// <summary>
        /// Creates one immutable set of whole-edge ordering constraints.
        /// </summary>
        public EdgeLayeringConstraints(IReadOnlyList<RuntimeExecutionTaskId> groupsToRenderAbove, IReadOnlyList<RuntimeExecutionTaskId> groupsToRenderBelow)
        {
            GroupsToRenderAbove = groupsToRenderAbove ?? throw new ArgumentNullException(nameof(groupsToRenderAbove));
            GroupsToRenderBelow = groupsToRenderBelow ?? throw new ArgumentNullException(nameof(groupsToRenderBelow));
        }

        /// <summary>
        /// Gets the visible groups that a whole edge must render above because one endpoint belongs to their subtree.
        /// </summary>
        public IReadOnlyList<RuntimeExecutionTaskId> GroupsToRenderAbove { get; }

        /// <summary>
        /// Gets the visible groups that a whole edge should render below because the route crosses them without belonging
        /// to their subtree.
        /// </summary>
        public IReadOnlyList<RuntimeExecutionTaskId> GroupsToRenderBelow { get; }
    }

    /// <summary>
    /// Defines the minimum width used for graph nodes so short titles still read as graph nodes rather than badges.
    /// </summary>
    public const double NodeMinWidth = 156;

    /// <summary>
    /// Defines the fixed height used for leaf task cards.
    /// </summary>
    public const double NodeHeight = 100;

    /// <summary>
    /// Defines the fixed height reserved for a group-container header.
    /// </summary>
    public const double GroupHeaderHeight = 56;

    /// <summary>
    /// Defines the padding between a group-container border and its child items.
    /// </summary>
    public const double GroupPadding = 18;

    /* Column spacing stays generous enough for status glows and dependency elbows even when task cards shrink toward
       their content width. */
    private const double ColumnGap = 84;
    private const double RowGap = 30;
    private readonly RangeObservableCollection<ExecutionNodeViewModel> _nodes = new();
    private readonly RangeObservableCollection<ExecutionEdgeViewModel> _edges = new();
    private readonly Dictionary<RuntimeExecutionTaskId, ExecutionNodeViewModel> _nodesById = new();
    private readonly Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> _childrenByParentId = new();
    private readonly Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId?> _parentByTaskId = new();
    private readonly Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTask> _rawTasksById = new();
    private readonly Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> _rawChildrenByParentId = new();
    private readonly Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> _visibleTaskIdByRawTaskId = new();
    private readonly Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> _ownedRawTaskIdsByVisibleNodeId = new();
    private readonly Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> _leafDescendantsByGroupId = new();
    private readonly Dictionary<RuntimeExecutionTaskId, double> _measuredNodeWidths = new();
    private readonly List<RuntimeExecutionTaskId> _rootTaskIds = new();
    private IReadOnlyDictionary<RuntimeExecutionTaskId, ExecutionTaskViewModel> _tasksById = new Dictionary<RuntimeExecutionTaskId, ExecutionTaskViewModel>();
    private IReadOnlyList<RuntimeExecutionTask>? _sourceTasks;
    private ExecutionNodeViewModel? _selectedNode;
    private double _canvasWidth;
    private double _canvasHeight;
    private bool _isUpdatingGraph;
    private bool _revealHiddenTasks;
    private StructureLayeringSnapshot _structureLayering = new(new List<ExecutionNodeViewModel>(), new Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), EdgeLayeringConstraints>());

    /// <summary>
    /// Formats one optional duration using the compact runtime clock style shared by the workspace header and graph metrics.
    /// </summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration == null)
        {
            return "--:--";
        }

        TimeSpan resolvedDuration = duration.Value;
        if (resolvedDuration < TimeSpan.Zero)
        {
            resolvedDuration = TimeSpan.Zero;
        }

        return resolvedDuration.TotalHours >= 1
            ? resolvedDuration.ToString(@"h\:mm\:ss")
            : resolvedDuration.ToString(@"mm\:ss");
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
    /// Gets the current structural layering snapshot used by the canvas to interleave groups and whole edges.
    /// </summary>
    public StructureLayeringSnapshot StructureLayering => _structureLayering;

    /// <summary>
    /// Gets whether the graph is currently rebuilding its internal collections and bounds as one bulk update.
    /// </summary>
    public bool IsUpdatingGraph
    {
        get => _isUpdatingGraph;
        private set => SetProperty(ref _isUpdatingGraph, value);
    }

    /// <summary>
    /// Gets whether the graph currently reveals tasks that are normally collapsed out of the visible hierarchy.
    /// </summary>
    public bool RevealHiddenTasks
    {
        get => _revealHiddenTasks;
        private set => SetProperty(ref _revealHiddenTasks, value);
    }

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
            RaisePropertyChanged(nameof(SelectedTaskId));
            RaisePropertyChanged(nameof(HasSelectedNode));
        }
    }

    /// <summary>
    /// Gets whether the graph currently has a selected graph node.
    /// </summary>
    public bool HasSelectedNode => SelectedNode != null;

    /// <summary>
    /// Gets the selected task id for details and status binding.
    /// </summary>
    public RuntimeExecutionTaskId? SelectedTaskId => SelectedNode?.Id;

    /// <summary>
    /// Returns the selected task id plus all descendant task ids so the UI can show one hierarchical task subtree
    /// without treating parent tasks specially at runtime.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GetSelectedLogTaskIds()
    {
        if (SelectedNode == null)
        {
            return Array.Empty<RuntimeExecutionTaskId>();
        }

        return GetTaskSubtreeIds(SelectedNode.Id).ToList();
    }

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
    /// Replaces the rendered graph with the provided task set and recomputes the nested comment-container layout.
    /// </summary>
    public void SetGraph(IReadOnlyList<RuntimeExecutionTask>? tasks)
    {
        /* Trace plan-graph rebuild phases separately so option-edit latency can be attributed to lookup creation, layout,
           metrics, edge construction, or selection restoration rather than treating graph refresh as one opaque cost. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraph.SetGraph")
            .SetTag("plan.has_result", tasks != null)
            .SetTag("plan.task.count", tasks?.Count ?? 0);

        _sourceTasks = tasks?.ToList();
        Dictionary<RuntimeExecutionTaskId, double> retainedMeasuredNodeWidths = tasks == null
            ? new Dictionary<RuntimeExecutionTaskId, double>()
            : RetainMeasuredNodeWidths(tasks);
        IsUpdatingGraph = true;
        try
        {
            _nodes.Clear();
            _edges.Clear();
            _nodesById.Clear();
            _childrenByParentId.Clear();
            _parentByTaskId.Clear();
            _rawTasksById.Clear();
            _rawChildrenByParentId.Clear();
            _visibleTaskIdByRawTaskId.Clear();
            _ownedRawTaskIdsByVisibleNodeId.Clear();
            _leafDescendantsByGroupId.Clear();
            _measuredNodeWidths.Clear();
            _structureLayering = new StructureLayeringSnapshot(new List<ExecutionNodeViewModel>(), new Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), EdgeLayeringConstraints>());
            foreach ((RuntimeExecutionTaskId taskId, double width) in retainedMeasuredNodeWidths)
            {
                _measuredNodeWidths[taskId] = width;
            }

            _rootTaskIds.Clear();

            if (tasks == null)
            {
                CanvasWidth = NodeMinWidth;
                CanvasHeight = NodeHeight;
                SelectNode(null);
                return;
            }

            using (PerformanceActivityScope buildNodesActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.BuildNodeLookup"))
            {
                buildNodesActivity.SetTag("plan.task.count", tasks.Count);
                BuildNodeLookup(tasks);
            }

            using (PerformanceActivityScope buildHierarchyActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.BuildHierarchyLookups"))
            {
                BuildHierarchyLookups(tasks);
            }

            RecalculateLayout(buildEdges: true);

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
    /// Updates whether the graph should reveal tasks that are normally collapsed and rebuilds the current projection when
    /// that preference changes.
    /// </summary>
    public void SetRevealHiddenTasks(bool revealHiddenTasks)
    {
        if (RevealHiddenTasks == revealHiddenTasks)
        {
            return;
        }

        RevealHiddenTasks = revealHiddenTasks;
        SetGraph(_sourceTasks);
    }

    /// <summary>
    /// Selects one graph node in the rendered plan.
    /// </summary>
    public void SelectNode(ExecutionNodeViewModel? node)
    {
        SelectedNode = node;
    }

    /// <summary>
    /// Attaches the shared Avalonia task view-model registry that graph nodes should wrap instead of constructing local copies.
    /// </summary>
    public void AttachTasks(IReadOnlyDictionary<RuntimeExecutionTaskId, ExecutionTaskViewModel> tasksById)
    {
        _tasksById = tasksById ?? throw new ArgumentNullException(nameof(tasksById));
    }

    /// <summary>
    /// Seeds measured widths from another graph for matching visible task ids so a newly opened runtime graph can reuse
    /// the already-measured preview widths instead of forcing a full first-pass width measurement for the same nodes.
    /// </summary>
    public void ImportMeasuredNodeWidthsFrom(ExecutionGraphViewModel? sourceGraph, IReadOnlyList<RuntimeExecutionTask>? tasks)
    {
        if (sourceGraph == null || tasks == null)
        {
            return;
        }

        /* Group containers size themselves from dynamic header content such as metrics chips and status labels, so only
           leaf-task widths are worth importing from another graph snapshot. Carrying group widths forward causes stale
           header measurements to clip once the live runtime header grows wider than the cached preview width. */
        foreach ((RuntimeExecutionTaskId taskId, double width) in sourceGraph.RetainMeasuredNodeWidths(tasks))
        {
            _measuredNodeWidths[taskId] = width;
        }
    }

    /// <summary>
    /// Recomputes graph layout using the current measured node widths after the canvas has updated them from real XAML
    /// control measurement.
    /// </summary>
    public void Relayout()
    {
        if (_nodes.Count == 0)
        {
            return;
        }

        IsUpdatingGraph = true;
        try
        {
            RecalculateLayout(buildEdges: false);
        }
        finally
        {
            IsUpdatingGraph = false;
        }
    }

    /// <summary>
    /// Stores the measured natural width for one rendered graph control so layout can use the XAML-owned size instead of
    /// view-model text heuristics.
    /// </summary>
    public bool SetMeasuredNodeWidth(RuntimeExecutionTaskId taskId, double width)
    {
        double measuredWidth = Math.Max(NodeMinWidth, width);
        if (_measuredNodeWidths.TryGetValue(taskId, out double existingWidth) && Math.Abs(existingWidth - measuredWidth) <= 0.5)
        {
            return false;
        }

        _measuredNodeWidths[taskId] = measuredWidth;
        return true;
    }

    /// <summary>
    /// Raises selected-node notifications when an underlying shared task VM changed in a way that could affect details UI.
    /// </summary>
    public void NotifyTaskStateChanged(RuntimeExecutionTaskId taskId)
    {
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("ExecutionGraph.NotifyTaskStateChanged")
            .SetTag("task.id", taskId.Value);
        if (!_nodesById.TryGetValue(taskId, out ExecutionNodeViewModel? node))
        {
            activity.SetTag("node.found", false);
            return;
        }

        activity.SetTag("node.found", true)
            .SetTag("selected.node.id", SelectedNode?.Id.Value ?? string.Empty)
            .SetTag("selected.node.is_container", SelectedNode?.IsContainer ?? false)
            .SetTag("node.is_selected", ReferenceEquals(SelectedNode, node));

        if (ReferenceEquals(SelectedNode, node) || (SelectedNode != null && SelectedNode.IsContainer))
        {
            activity.SetTag("selected_node.invalidated", true);
            RaisePropertyChanged(nameof(SelectedNode));
            return;
        }

        activity.SetTag("selected_node.invalidated", false);
    }

    /// <summary>
    /// Returns whether the provided node still needs one hidden-host measurement pass before the layout can trust its
    /// rendered width.
    /// </summary>
    internal bool NeedsMeasuredNodeWidth(RuntimeExecutionTaskId taskId)
    {
        /* Group containers keep dynamic header chrome that can widen as metrics/status change during execution, so they
           always opt back into the hidden-host measurement pass instead of trusting an older cached width. Leaf cards are
           far more numerous, so they continue reusing cached widths until they first appear or their cache is absent. */
        return !_nodesById.TryGetValue(taskId, out ExecutionNodeViewModel? node) ||
               node.IsContainer ||
               !_measuredNodeWidths.ContainsKey(taskId);
    }

    /// <summary>
    /// Materializes one graph node view model for every task in the source plan.
    /// </summary>
    private void BuildNodeLookup(IReadOnlyList<RuntimeExecutionTask> tasks)
    {
        /* The canvas only renders visible tasks. Hidden authored/internal tasks still exist in the raw runtime graph,
           but they collapse into the nearest visible ancestor unless the user reveals hidden nodes globally. */
        List<ExecutionNodeViewModel> nodes = new(tasks.Count);
        foreach (RuntimeExecutionTask task in tasks)
        {
            if (!ShouldRenderTask(task))
            {
                continue;
            }

            if (!_tasksById.TryGetValue(task.Id, out ExecutionTaskViewModel? taskViewModel))
            {
                throw new InvalidOperationException($"No shared ExecutionTaskViewModel exists for task '{task.Id}'.");
            }

            ExecutionNodeViewModel node = new(taskViewModel);
            nodes.Add(node);
            _nodesById[task.Id] = node;
        }

        _nodes.AddRange(nodes);
    }

    /// <summary>
    /// Preserves measured widths for visible node ids that survive into the next graph snapshot so runtime child-task
    /// insertion only measures genuinely new nodes instead of forcing every existing card through hidden-host layout
    /// again.
    /// </summary>
    private Dictionary<RuntimeExecutionTaskId, double> RetainMeasuredNodeWidths(IReadOnlyList<RuntimeExecutionTask> tasks)
    {
        /* Width reuse should follow the visible graph shape, not the raw authored hierarchy. Many visible leaf cards own
           hidden body children, which makes them raw parents even though they still render as leaf cards. Excluding raw
           parents here drops most reusable card widths and forces the session graph to rebuild a full hidden measurement
           host on first render. */
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTask> visibleTasksById = tasks
            .Where(ShouldRenderTask)
            .ToDictionary(task => task.Id);
        HashSet<RuntimeExecutionTaskId> visibleParentIds = visibleTasksById.Values
            .Where(task => task.ParentId != null && visibleTasksById.ContainsKey(task.ParentId.Value))
            .Select(task => task.ParentId!.Value)
            .ToHashSet();
        HashSet<RuntimeExecutionTaskId> reusableLeafIds = visibleTasksById.Keys
            .Where(taskId => !visibleParentIds.Contains(taskId))
            .ToHashSet();

        return _measuredNodeWidths
            .Where(entry => reusableLeafIds.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    /// <summary>
    /// Builds the parent and child lookup tables that drive nested group-container layout.
    /// </summary>
    private void BuildHierarchyLookups(IEnumerable<RuntimeExecutionTask> tasks)
    {
        /* Build the raw hierarchy first so the graph can collapse hidden tasks without losing subtree ownership,
           dependency remapping, or selection-scoped logs. */
        foreach (RuntimeExecutionTask task in tasks)
        {
            _rawTasksById[task.Id] = task;
            if (task.ParentId is not RuntimeExecutionTaskId parentId)
            {
                continue;
            }

            if (!_rawChildrenByParentId.TryGetValue(parentId, out List<RuntimeExecutionTaskId>? childIds))
            {
                childIds = new List<RuntimeExecutionTaskId>();
                _rawChildrenByParentId[parentId] = childIds;
            }

            childIds.Add(task.Id);
        }

        /* Project visible parents by climbing through any hidden ancestors so containment frames connect visible tasks
           directly even when intermediate implementation-detail nodes still exist in the runtime tree. */
        foreach (RuntimeExecutionTask task in tasks.Where(ShouldRenderTask))
        {
            RuntimeExecutionTaskId? visibleParentId = FindVisibleParentId(task);
            _parentByTaskId[task.Id] = visibleParentId;
            if (visibleParentId is not RuntimeExecutionTaskId resolvedVisibleParentId)
            {
                _rootTaskIds.Add(task.Id);
                continue;
            }

            if (!_childrenByParentId.TryGetValue(resolvedVisibleParentId, out List<RuntimeExecutionTaskId>? childIds))
            {
                childIds = new List<RuntimeExecutionTaskId>();
                _childrenByParentId[resolvedVisibleParentId] = childIds;
            }

            childIds.Add(task.Id);
        }

        /* Record which raw tasks belong to each visible node's collapsed segment so subtree log selection can still
           include hidden descendants even though the graph renders only visible tasks. */
        foreach (RuntimeExecutionTask rootTask in tasks.Where(task => task.ParentId == null))
        {
            MapRawTasksToVisibleOwners(rootTask.Id, currentVisibleOwnerId: null);
        }
    }

    /// <summary>
    /// Lays out one sibling set as dependency stages: sequential stages flow left-to-right while tasks that can run in
    /// parallel within the same stage stack vertically.
    /// </summary>
    private LayoutBounds LayoutDirectChildren(RuntimeExecutionTaskId? parentId, double originX, double originY)
    {
        IReadOnlyList<ExecutionNodeViewModel> children = GetDirectChildren(parentId);
        if (children.Count == 0)
        {
            return LayoutBounds.Empty;
        }

        IReadOnlyList<IReadOnlyList<ExecutionNodeViewModel>> stages = BuildSiblingStages(children);
        if (stages.Count == 1)
        {
            return LayoutChildrenVertically(children, originX, originY);
        }

        return LayoutSiblingStages(stages, originX, originY);
    }

    /// <summary>
    /// Partitions one sibling set into dependency stages so siblings with no remaining intra-sibling dependencies share a
    /// stage and later dependents flow into columns to the right.
    /// </summary>
    private IReadOnlyList<IReadOnlyList<ExecutionNodeViewModel>> BuildSiblingStages(IReadOnlyList<ExecutionNodeViewModel> children)
    {
        if (children.Count <= 1)
        {
            return new[] { children };
        }

        Dictionary<RuntimeExecutionTaskId, ExecutionNodeViewModel> childrenById = children.ToDictionary(child => child.Id);
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> siblingOwnerByTaskId = BuildSiblingOwnerMap(children);
        Dictionary<RuntimeExecutionTaskId, HashSet<RuntimeExecutionTaskId>> remainingDependencies = children.ToDictionary(
            child => child.Id,
            child => child.Task.Task.Dependencies
                .Select(dependencyId => siblingOwnerByTaskId.TryGetValue(dependencyId, out RuntimeExecutionTaskId ownerId) ? ownerId : default)
                .Where(ownerId => ownerId != default && ownerId != child.Id && childrenById.ContainsKey(ownerId))
                .ToHashSet());
        List<ExecutionNodeViewModel> remainingChildren = children.ToList();
        List<IReadOnlyList<ExecutionNodeViewModel>> stages = new();

        while (remainingChildren.Count > 0)
        {
            List<ExecutionNodeViewModel> stage = remainingChildren
                .Where(child => remainingDependencies[child.Id].Count == 0)
                .ToList();

            if (stage.Count == 0)
            {
                /* Valid execution plans are DAGs, so this fallback should only protect the UI from malformed data. When a
                   cycle slips through, preserve authored order instead of failing layout. */
                stage.Add(remainingChildren[0]);
            }

            stages.Add(stage);
            HashSet<RuntimeExecutionTaskId> completedStageIds = stage
                .Select(child => child.Id)
                .ToHashSet();
            remainingChildren.RemoveAll(child => completedStageIds.Contains(child.Id));

            foreach (HashSet<RuntimeExecutionTaskId> dependencySet in remainingDependencies.Values)
            {
                dependencySet.RemoveWhere(completedStageIds.Contains);
            }
        }

        return stages;
    }

    /// <summary>
    /// Maps every task id inside the provided sibling subtrees back to the direct sibling container that owns that task so
    /// dependencies on descendant body nodes still count as dependencies on the sibling stage.
    /// </summary>
    private Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> BuildSiblingOwnerMap(IReadOnlyList<ExecutionNodeViewModel> children)
    {
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> ownerMap = new();
        foreach (ExecutionNodeViewModel child in children)
        {
            foreach (RuntimeExecutionTaskId subtreeTaskId in EnumerateVisibleSubtreeRawTaskIds(child.Id))
            {
                ownerMap[subtreeTaskId] = child.Id;
            }
        }

        return ownerMap;
    }

    /// <summary>
    /// Enumerates one node id plus all descendant task ids in its subtree.
    /// </summary>
    private IEnumerable<RuntimeExecutionTaskId> EnumerateVisibleSubtreeRawTaskIds(RuntimeExecutionTaskId rootId)
    {
        if (_ownedRawTaskIdsByVisibleNodeId.TryGetValue(rootId, out List<RuntimeExecutionTaskId>? ownedTaskIds))
        {
            foreach (RuntimeExecutionTaskId taskId in ownedTaskIds)
            {
                yield return taskId;
            }
        }

        foreach (ExecutionNodeViewModel child in GetDirectChildren(rootId))
        {
            foreach (RuntimeExecutionTaskId childId in EnumerateVisibleSubtreeRawTaskIds(child.Id))
            {
                yield return childId;
            }
        }
    }

    /// <summary>
    /// Lays out one sibling set vertically, treating each child subtree like its own stacked track.
    /// </summary>
    private LayoutBounds LayoutChildrenVertically(IReadOnlyList<ExecutionNodeViewModel> children, double originX, double originY)
    {
        LayoutBounds stackedBounds = LayoutBounds.Empty;
        double currentY = originY;

        foreach (ExecutionNodeViewModel child in children)
        {
            LayoutBounds childBounds = LayoutNodeSubtree(child, originX, currentY);
            stackedBounds = stackedBounds.Include(childBounds);
            currentY = childBounds.Bottom + RowGap;
        }

        return stackedBounds;
    }

    /// <summary>
    /// Lays out dependency stages left-to-right, stacking the tasks within each stage vertically and then centering the
    /// shorter stage columns within the tallest column.
    /// </summary>
    private LayoutBounds LayoutSiblingStages(IReadOnlyList<IReadOnlyList<ExecutionNodeViewModel>> stages, double originX, double originY)
    {
        LayoutBounds bounds = LayoutBounds.Empty;
        double currentX = originX;
        List<(IReadOnlyList<ExecutionNodeViewModel> Stage, LayoutBounds Bounds)> laidOutStages = new(stages.Count);
        foreach (IReadOnlyList<ExecutionNodeViewModel> stage in stages)
        {
            LayoutBounds stageBounds = LayoutChildrenVertically(stage, currentX, originY);
            laidOutStages.Add((stage, stageBounds));
            bounds = bounds.Include(stageBounds);
            currentX = stageBounds.Right + ColumnGap;
        }

        return CenterStageColumnsVertically(laidOutStages, bounds);
    }

    /// <summary>
    /// Lays out either a leaf or group subtree for one direct child without duplicating the node-kind branch at every
    /// sibling-layout call site.
    /// </summary>
    private LayoutBounds LayoutNodeSubtree(ExecutionNodeViewModel node, double x, double y)
    {
        return HasChildren(node.Id)
            ? LayoutGroupNode(node, x, y)
            : LayoutLeafNode(node, x, y);
    }

    /// <summary>
    /// Lays out one leaf task as a normal card.
    /// </summary>
    private LayoutBounds LayoutLeafNode(ExecutionNodeViewModel node, double x, double y)
    {
        double width = GetMeasuredNodeWidth(node.Id);
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
            double emptyGroupWidth = GetMeasuredNodeWidth(node.Id);
            node.SetBounds(x, y, emptyGroupWidth, NodeHeight);
            node.SetHierarchyMetrics(directChildCount: 0, descendantTaskCount: 0, summaryText: "Empty task group");
            return new LayoutBounds(x, y, emptyGroupWidth, NodeHeight);
        }

        LayoutDirectChildren(node.Id, x + GroupPadding, y + GroupHeaderHeight + GroupPadding);
        /* Size the outer frame from the actual laid-out direct-child rectangles instead of the aggregated layout-bounds
           helper so nested groups use the same final child extents that the canvas renders, including any measured-width
           updates that happened after the child subtree was laid out. */
        double maxChildRight = children.Max(child => child.X + child.Width);
        double maxChildBottom = children.Max(child => child.Y + child.Height);
        double width = Math.Max(
            GetMeasuredNodeWidth(node.Id),
            Math.Max(NodeMinWidth, (maxChildRight - x) + GroupPadding));
        double height = Math.Max(NodeHeight, (maxChildBottom - y) + GroupPadding);
        CenterDirectChildrenInGroup(node.Id, x, y, width, height, children);
        node.SetBounds(x, y, width, height);
        return new LayoutBounds(x, y, width, height);
    }

    /// <summary>
    /// Centers one already-laid-out direct-child subtree inside the available content area of its parent group while
    /// preserving the internal relative positions of all descendants.
    /// </summary>
    private void CenterDirectChildrenInGroup(RuntimeExecutionTaskId groupId, double groupX, double groupY, double groupWidth, double groupHeight, IReadOnlyList<ExecutionNodeViewModel> children)
    {
        if (children.Count == 0)
        {
            return;
        }

        /* The group frame reserves one padded content rectangle beneath the header. Center the direct-child subtree
           inside that rectangle so compact subtrees do not cling to the top-left corner when the group grows wider or
           taller than the child content requires. */
        double minChildX = children.Min(child => child.X);
        double minChildY = children.Min(child => child.Y);
        double maxChildRight = children.Max(child => child.X + child.Width);
        double maxChildBottom = children.Max(child => child.Y + child.Height);
        double subtreeWidth = maxChildRight - minChildX;
        double subtreeHeight = maxChildBottom - minChildY;

        double contentX = groupX + GroupPadding;
        double contentY = groupY + GroupHeaderHeight + GroupPadding;
        double contentWidth = Math.Max(0, groupWidth - (GroupPadding * 2));
        double contentHeight = Math.Max(0, groupHeight - GroupHeaderHeight - (GroupPadding * 2));
        double desiredChildX = contentX + Math.Max(0, (contentWidth - subtreeWidth) / 2.0);
        double desiredChildY = contentY + Math.Max(0, (contentHeight - subtreeHeight) / 2.0);
        double deltaX = desiredChildX - minChildX;
        double deltaY = desiredChildY - minChildY;
        if (Math.Abs(deltaX) <= 0.5 && Math.Abs(deltaY) <= 0.5)
        {
            return;
        }

        foreach (ExecutionNodeViewModel child in children)
        {
            OffsetSubtree(child.Id, deltaX, deltaY);
        }
    }

    /// <summary>
    /// Centers each stage column within the tallest sibling-stage column so shorter parallel stacks do not cling to the top
    /// edge when a neighboring stage grows taller.
    /// </summary>
    private LayoutBounds CenterStageColumnsVertically(IReadOnlyList<(IReadOnlyList<ExecutionNodeViewModel> Stage, LayoutBounds Bounds)> stages, LayoutBounds rowBounds)
    {
        if (stages.Count <= 1 || rowBounds.IsEmpty)
        {
            return rowBounds;
        }

        double rowHeight = stages.Max(stage => stage.Bounds.Height);
        bool anyOffsetApplied = false;
        foreach ((IReadOnlyList<ExecutionNodeViewModel> stage, LayoutBounds bounds) in stages)
        {
            double deltaY = (rowHeight - bounds.Height) / 2.0;
            if (Math.Abs(deltaY) <= 0.5)
            {
                continue;
            }

            foreach (ExecutionNodeViewModel node in stage)
            {
                OffsetSubtree(node.Id, 0, deltaY);
            }

            anyOffsetApplied = true;
        }

        if (!anyOffsetApplied)
        {
            return rowBounds;
        }

        LayoutBounds centeredBounds = LayoutBounds.Empty;
        foreach ((IReadOnlyList<ExecutionNodeViewModel> stage, _) in stages)
        {
            foreach (ExecutionNodeViewModel node in stage)
            {
                centeredBounds = centeredBounds.Include(GetSubtreeBounds(node.Id));
            }
        }

        return centeredBounds;
    }

    /// <summary>
    /// Offsets one node and all of its descendants by the same amount so centering a parent sibling set preserves the
    /// subtree's internal layout.
    /// </summary>
    private void OffsetSubtree(RuntimeExecutionTaskId rootId, double deltaX, double deltaY)
    {
        if (!_nodesById.TryGetValue(rootId, out ExecutionNodeViewModel? node))
        {
            return;
        }

        node.SetBounds(node.X + deltaX, node.Y + deltaY, node.Width, node.Height);
        foreach (ExecutionNodeViewModel child in GetDirectChildren(rootId))
        {
            OffsetSubtree(child.Id, deltaX, deltaY);
        }
    }

    /// <summary>
    /// Returns the bounds of one node subtree from the node and all descendants after any centering offsets have been
    /// applied.
    /// </summary>
    private LayoutBounds GetSubtreeBounds(RuntimeExecutionTaskId rootId)
    {
        if (!_nodesById.TryGetValue(rootId, out ExecutionNodeViewModel? node))
        {
            return LayoutBounds.Empty;
        }

        LayoutBounds bounds = new(node.X, node.Y, node.Width, node.Height);
        foreach (ExecutionNodeViewModel child in GetDirectChildren(rootId))
        {
            bounds = bounds.Include(GetSubtreeBounds(child.Id));
        }

        return bounds;
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
            List<RuntimeExecutionTaskId> leafDescendants = GetLeafDescendantIds(node.Id).ToList();
            _leafDescendantsByGroupId[node.Id] = leafDescendants;

            string summaryText = directChildren.Count == 0
                ? "No child tasks"
                : $"{directChildren.Count} child item{(directChildren.Count == 1 ? string.Empty : "s")} - {leafDescendants.Count} runnable task{(leafDescendants.Count == 1 ? string.Empty : "s")}";

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
    private void RefreshAncestorGroupStatuses(RuntimeExecutionTaskId taskId)
    {
        RuntimeExecutionTaskId? currentParentId = _parentByTaskId.TryGetValue(taskId, out RuntimeExecutionTaskId? parentId) ? parentId : null;
        while (currentParentId is RuntimeExecutionTaskId parent && _nodesById.TryGetValue(parent, out ExecutionNodeViewModel? group))
        {
            ApplyGroupRollupStatus(group);
            currentParentId = _parentByTaskId.TryGetValue(parent, out RuntimeExecutionTaskId? nextParentId) ? nextParentId : null;
        }
    }

    /// <summary>
    /// Builds the visible dependency edges between laid out graph nodes after layout has produced their coordinates.
    /// Group containers participate just like leaf task cards so dependencies remain visible even when tasks own child
    /// subtrees.
    /// </summary>
    private void BuildDependencyEdges()
    {
        List<ExecutionEdgeViewModel> edges = new();
        foreach (ExecutionNodeViewModel target in _nodes)
        {
            foreach (RuntimeExecutionTaskId dependencyId in target.Task.Task.Dependencies)
            {
                RuntimeExecutionTaskId? visibleDependencyId = GetVisibleTaskId(dependencyId);
                if (visibleDependencyId == null || visibleDependencyId == target.Id)
                {
                    continue;
                }

                /* Visible containment already communicates ancestor/descendant structure, so suppress dependency lines
                   inside the same visible branch after hidden-task collapsing remaps internal callback dependencies onto
                   their nearest visible owners. */
                if (IsVisibleAncestor(visibleDependencyId.Value, target.Id) || IsVisibleAncestor(target.Id, visibleDependencyId.Value))
                {
                    continue;
                }

                if (_nodesById.TryGetValue(visibleDependencyId.Value, out ExecutionNodeViewModel? source))
                {
                    edges.Add(new ExecutionEdgeViewModel(source, target));
                }
            }
        }

        /* Collapsing multiple hidden tasks can make several raw dependencies resolve to the same visible edge, so dedupe
           the rendered edge list by visible source/target pair before exposing it to the canvas. */
        _edges.AddRange(edges
            .GroupBy(edge => (edge.Source.Id, edge.Target.Id))
            .Select(group => group.First()));
        _structureLayering = BuildStructureLayeringSnapshot();
    }

    /// <summary>
    /// Builds the graph-owned structural layering snapshot so the canvas can ask the graph for whole-edge ordering
    /// answers without storing that policy on every edge view model.
    /// </summary>
    private StructureLayeringSnapshot BuildStructureLayeringSnapshot()
    {
        List<ExecutionNodeViewModel> orderedGroups = _nodes
            .Where(node => node.IsContainer)
            .OrderByDescending(node => node.Width * node.Height)
            .ToList();
        Dictionary<(RuntimeExecutionTaskId SourceId, RuntimeExecutionTaskId TargetId), EdgeLayeringConstraints> edgeConstraints = _edges
            .ToDictionary(edge => (edge.Source.Id, edge.Target.Id), ResolveWholeEdgeLayering);
        return new StructureLayeringSnapshot(orderedGroups, edgeConstraints);
    }

    /// <summary>
    /// Resolves the visible groups that constrain one whole-edge draw slot so the canvas can interleave the edge between
    /// unrelated group containers without segment-level clipping.
    /// </summary>
    private EdgeLayeringConstraints ResolveWholeEdgeLayering(ExecutionEdgeViewModel edge)
    {
        HashSet<RuntimeExecutionTaskId> groupsToRenderAbove = GetVisibleAncestorIds(edge.Source.Id)
            .Concat(GetVisibleAncestorIds(edge.Target.Id))
            .ToHashSet();
        List<RuntimeExecutionTaskId> groupsToRenderBelow = _nodes
            .Where(node => node.IsContainer)
            .Select(node => node.Id)
            .Where(groupId => !groupsToRenderAbove.Contains(groupId))
            .Where(groupId => DoesEdgeCrossGroup(edge, _nodesById[groupId]))
            .OrderBy(GetVisibleDepth)
            .ToList();
        return new EdgeLayeringConstraints(groupsToRenderAbove.OrderBy(GetVisibleDepth).ToList(), groupsToRenderBelow);
    }

    /// <summary>
    /// Enumerates the visible ancestor chain for one node id using the collapsed hierarchy projection rather than raw task
    /// parents.
    /// </summary>
    private IEnumerable<RuntimeExecutionTaskId> GetVisibleAncestorIds(RuntimeExecutionTaskId nodeId)
    {
        RuntimeExecutionTaskId? currentParentId = _parentByTaskId.TryGetValue(nodeId, out RuntimeExecutionTaskId? parentId)
            ? parentId
            : null;
        while (currentParentId is RuntimeExecutionTaskId resolvedParentId)
        {
            yield return resolvedParentId;
            currentParentId = _parentByTaskId.TryGetValue(resolvedParentId, out RuntimeExecutionTaskId? nextParentId)
                ? nextParentId
                : null;
        }
    }

    /// <summary>
    /// Returns the visible hierarchy depth for one node so layering constraints can be ordered from outer groups to inner
    /// groups consistently.
    /// </summary>
    private int GetVisibleDepth(RuntimeExecutionTaskId nodeId)
    {
        int depth = 0;
        RuntimeExecutionTaskId? currentParentId = _parentByTaskId.TryGetValue(nodeId, out RuntimeExecutionTaskId? parentId)
            ? parentId
            : null;
        while (currentParentId is RuntimeExecutionTaskId resolvedParentId)
        {
            depth++;
            currentParentId = _parentByTaskId.TryGetValue(resolvedParentId, out RuntimeExecutionTaskId? nextParentId)
                ? nextParentId
                : null;
        }

        return depth;
    }

    /// <summary>
    /// Returns whether the routed edge intersects the target group rectangle at all, which is the condition that makes the
    /// group relevant as an occluding container for whole-edge ordering.
    /// </summary>
    private static bool DoesEdgeCrossGroup(ExecutionEdgeViewModel edge, ExecutionNodeViewModel group)
    {
        Rect groupRect = new(group.X, group.Y, group.Width, group.Height);
        if (groupRect.Width <= 0 || groupRect.Height <= 0)
        {
            return false;
        }

        Rect routeBounds = new(
            Math.Min(edge.Source.X + edge.Source.Width, edge.Target.X),
            Math.Min(edge.Source.Y, edge.Target.Y),
            Math.Abs(edge.Target.X - (edge.Source.X + edge.Source.Width)),
            Math.Max(edge.Source.Y + edge.Source.Height, edge.Target.Y + edge.Target.Height) - Math.Min(edge.Source.Y, edge.Target.Y));
        return routeBounds.Intersects(groupRect);
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
    /// Restores the default graph selection after the plan changes by selecting the one real root task when no previous
    /// selection can be preserved.
    /// </summary>
    private void RestoreSelection()
    {
        if (_nodes.Count == 0)
        {
            SelectNode(null);
            return;
        }

        RuntimeExecutionTaskId? previouslySelectedTaskId = SelectedNode?.Id;
        RuntimeExecutionTaskId? restoredSelectionId = previouslySelectedTaskId == null
            ? null
            : ResolveVisibleSelectionId(previouslySelectedTaskId.Value);
        if (restoredSelectionId != null && _nodesById.TryGetValue(restoredSelectionId.Value, out ExecutionNodeViewModel? existingSelection))
        {
            SelectNode(existingSelection);
            return;
        }

        RuntimeExecutionTaskId? fallbackRootId = _rootTaskIds.Count > 0 ? _rootTaskIds[0] : null;
        SelectNode(fallbackRootId != null && _nodesById.TryGetValue(fallbackRootId.Value, out ExecutionNodeViewModel? rootSelection)
            ? rootSelection
            : _nodes.FirstOrDefault());
    }

    /// <summary>
    /// Returns the direct child graph nodes for the provided parent task id.
    /// </summary>
    private IReadOnlyList<ExecutionNodeViewModel> GetDirectChildren(RuntimeExecutionTaskId? parentId)
    {
        if (parentId == null)
        {
            return _rootTaskIds.Select(childId => _nodesById[childId]).ToList();
        }

        if (!_childrenByParentId.TryGetValue(parentId.Value, out List<RuntimeExecutionTaskId>? childIds))
        {
            return Array.Empty<ExecutionNodeViewModel>();
        }

        return childIds.Select(childId => _nodesById[childId]).ToList();
    }

    /// <summary>
    /// Enumerates all descendant runnable task ids beneath the provided container task id.
    /// </summary>
    private IEnumerable<RuntimeExecutionTaskId> GetLeafDescendantIds(RuntimeExecutionTaskId groupId)
    {
        if (!_childrenByParentId.TryGetValue(groupId, out List<RuntimeExecutionTaskId>? childIds))
        {
            yield break;
        }

        foreach (RuntimeExecutionTaskId childId in childIds)
        {
            ExecutionNodeViewModel child = _nodesById[childId];
            if (!HasChildren(childId))
            {
                yield return childId;
                continue;
            }

            foreach (RuntimeExecutionTaskId descendantId in GetLeafDescendantIds(childId))
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
    private bool HasChildren(RuntimeExecutionTaskId taskId)
    {
        return _childrenByParentId.TryGetValue(taskId, out List<RuntimeExecutionTaskId>? childIds) && childIds.Count > 0;
    }

    /// <summary>
    /// Returns whether one raw task should appear as a visible node in the current graph projection.
    /// </summary>
    private bool ShouldRenderTask(RuntimeExecutionTask task)
    {
        return RevealHiddenTasks || !task.IsHiddenInGraph;
    }

    /// <summary>
    /// Climbs the raw task ancestry until the nearest visible parent is found so collapsed hidden tasks do not create
    /// empty visible containers.
    /// </summary>
    private RuntimeExecutionTaskId? FindVisibleParentId(RuntimeExecutionTask task)
    {
        RuntimeExecutionTaskId? currentParentId = task.ParentId;
        while (currentParentId is RuntimeExecutionTaskId parentId)
        {
            if (_rawTasksById.TryGetValue(parentId, out RuntimeExecutionTask? parentTask) && ShouldRenderTask(parentTask))
            {
                return parentId;
            }

            currentParentId = _rawTasksById.TryGetValue(parentId, out parentTask)
                ? parentTask.ParentId
                : null;
        }

        return null;
    }

    /// <summary>
    /// Maps every raw task to the visible node that owns it in the collapsed graph so edge remapping and log selection
    /// can still reason about hidden tasks.
    /// </summary>
    private void MapRawTasksToVisibleOwners(RuntimeExecutionTaskId taskId, RuntimeExecutionTaskId? currentVisibleOwnerId)
    {
        bool isVisible = _nodesById.ContainsKey(taskId);
        RuntimeExecutionTaskId? nextVisibleOwnerId = isVisible ? taskId : currentVisibleOwnerId;
        if (nextVisibleOwnerId is RuntimeExecutionTaskId visibleOwnerId)
        {
            _visibleTaskIdByRawTaskId[taskId] = visibleOwnerId;
            if (!_ownedRawTaskIdsByVisibleNodeId.TryGetValue(visibleOwnerId, out List<RuntimeExecutionTaskId>? ownedTaskIds))
            {
                ownedTaskIds = new List<RuntimeExecutionTaskId>();
                _ownedRawTaskIdsByVisibleNodeId[visibleOwnerId] = ownedTaskIds;
            }

            ownedTaskIds.Add(taskId);
        }

        if (!_rawChildrenByParentId.TryGetValue(taskId, out List<RuntimeExecutionTaskId>? childIds))
        {
            return;
        }

        foreach (RuntimeExecutionTaskId childId in childIds)
        {
            MapRawTasksToVisibleOwners(childId, nextVisibleOwnerId);
        }
    }

    /// <summary>
    /// Resolves one raw task id to the visible node that represents it in the current collapsed graph.
    /// </summary>
    private RuntimeExecutionTaskId? GetVisibleTaskId(RuntimeExecutionTaskId rawTaskId)
    {
        return _visibleTaskIdByRawTaskId.TryGetValue(rawTaskId, out RuntimeExecutionTaskId visibleTaskId)
            ? visibleTaskId
            : null;
    }

    /// <summary>
    /// Returns whether one visible task currently sits in the ancestor chain of another visible task.
    /// </summary>
    private bool IsVisibleAncestor(RuntimeExecutionTaskId ancestorId, RuntimeExecutionTaskId descendantId)
    {
        RuntimeExecutionTaskId? currentParentId = _parentByTaskId.TryGetValue(descendantId, out RuntimeExecutionTaskId? parentId)
            ? parentId
            : null;
        while (currentParentId is RuntimeExecutionTaskId resolvedParentId)
        {
            if (resolvedParentId == ancestorId)
            {
                return true;
            }

            currentParentId = _parentByTaskId.TryGetValue(resolvedParentId, out RuntimeExecutionTaskId? nextParentId)
                ? nextParentId
                : null;
        }

        return false;
    }

    /// <summary>
    /// Restores selection to the nearest visible ancestor when a previously selected hidden task becomes collapsed by the
    /// current reveal preference.
    /// </summary>
    private RuntimeExecutionTaskId? ResolveVisibleSelectionId(RuntimeExecutionTaskId taskId)
    {
        if (_nodesById.ContainsKey(taskId))
        {
            return taskId;
        }

        RuntimeExecutionTaskId? currentTaskId = taskId;
        while (currentTaskId is RuntimeExecutionTaskId resolvedTaskId && _rawTasksById.TryGetValue(resolvedTaskId, out RuntimeExecutionTask? task))
        {
            if (_nodesById.ContainsKey(resolvedTaskId))
            {
                return resolvedTaskId;
            }

            currentTaskId = task.ParentId;
        }

        return null;
    }

    /// <summary>
    /// Returns the measured natural width for one node when available, falling back to the minimum graph-node width for
    /// pre-measure layout passes.
    /// </summary>
    private double GetMeasuredNodeWidth(RuntimeExecutionTaskId taskId)
    {
        return _measuredNodeWidths.TryGetValue(taskId, out double width)
            ? width
            : NodeMinWidth;
    }

    /// <summary>
    /// Recomputes node bounds, hierarchy summaries, optional dependency edges, and canvas size from the current graph
    /// structure plus the latest measured node widths.
    /// </summary>
    private void RecalculateLayout(bool buildEdges)
    {
        using (PerformanceActivityScope layoutActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.Layout"))
        {
            LayoutDirectChildren(parentId: null, originX: 24, originY: 24);
        }

        using (PerformanceActivityScope metricsActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.ApplyHierarchyMetrics"))
        {
            ApplyGroupHierarchyMetrics();
            ApplyGroupRollupStatuses();
        }

        if (buildEdges)
        {
            using (PerformanceActivityScope edgesActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.BuildDependencyEdges"))
            {
                BuildDependencyEdges();
                edgesActivity.SetTag("edge.count", _edges.Count);
            }
        }

        using (PerformanceActivityScope canvasActivity = PerformanceTelemetry.StartActivity("ExecutionGraph.UpdateCanvasSize"))
        {
            UpdateCanvasSize();
            canvasActivity.SetTag("canvas.width", CanvasWidth)
                .SetTag("canvas.height", CanvasHeight);
        }
    }

    /// <summary>
    /// Enumerates one task subtree including the selected task itself and every descendant task so log selection can be
    /// hierarchical without mutating runtime task ownership.
    /// </summary>
    private IEnumerable<RuntimeExecutionTaskId> GetTaskSubtreeIds(RuntimeExecutionTaskId rootId)
    {
        if (_ownedRawTaskIdsByVisibleNodeId.TryGetValue(rootId, out List<RuntimeExecutionTaskId>? ownedTaskIds))
        {
            foreach (RuntimeExecutionTaskId taskId in ownedTaskIds)
            {
                yield return taskId;
            }
        }

        if (!_childrenByParentId.TryGetValue(rootId, out List<RuntimeExecutionTaskId>? childIds))
        {
            yield break;
        }

        foreach (RuntimeExecutionTaskId childId in childIds)
        {
            foreach (RuntimeExecutionTaskId descendantId in GetTaskSubtreeIds(childId))
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
