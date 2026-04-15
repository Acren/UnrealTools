using System;
using System.Collections.Generic;
using System.Linq;
using RuntimeExecutionTask = LocalAutomation.Runtime.ExecutionTask;
using RuntimeExecutionTaskId = LocalAutomation.Runtime.ExecutionTaskId;

namespace LocalAutomation.Avalonia.ExecutionGraph;

/// <summary>
/// Represents the visible execution-graph hierarchy after hidden tasks have optionally been collapsed out of the UI.
/// </summary>
internal sealed class ExecutionGraphProjection
{
    /// <summary>
    /// Gets the empty projection used before a task snapshot has been loaded.
    /// </summary>
    public static ExecutionGraphProjection Empty { get; } = new(
        visibleTaskIds: Array.Empty<RuntimeExecutionTaskId>(),
        rootTaskIds: Array.Empty<RuntimeExecutionTaskId>(),
        rawTasksById: new Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTask>(),
        childrenByParentId: new Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>>(),
        parentByTaskId: new Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId?>(),
        visibleTaskIdByRawTaskId: new Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId>(),
        ownedRawTaskIdsByVisibleNodeId: new Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>>());

    private readonly HashSet<RuntimeExecutionTaskId> _visibleTaskIds;
    private readonly Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTask> _rawTasksById;
    private readonly Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> _childrenByParentId;
    private readonly Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> _visibleTaskIdByRawTaskId;
    private readonly Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> _ownedRawTaskIdsByVisibleNodeId;
    private readonly Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId[]> _visibleAncestorIdsByTaskId;

    /// <summary>
    /// Creates one projection from the provided raw task snapshot and hidden-task visibility policy.
    /// </summary>
    public static ExecutionGraphProjection Create(IReadOnlyList<RuntimeExecutionTask> tasks, bool revealHiddenTasks)
    {
        if (tasks == null)
        {
            throw new ArgumentNullException(nameof(tasks));
        }

        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTask> rawTasksById = new();
        Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> rawChildrenByParentId = new();
        foreach (RuntimeExecutionTask task in tasks)
        {
            rawTasksById[task.Id] = task;
            if (task.ParentId is not RuntimeExecutionTaskId parentId)
            {
                continue;
            }

            if (!rawChildrenByParentId.TryGetValue(parentId, out List<RuntimeExecutionTaskId>? childIds))
            {
                childIds = new List<RuntimeExecutionTaskId>();
                rawChildrenByParentId[parentId] = childIds;
            }

            childIds.Add(task.Id);
        }

        List<RuntimeExecutionTaskId> visibleTaskIds = new();
        Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> childrenByParentId = new();
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId?> parentByTaskId = new();
        List<RuntimeExecutionTaskId> rootTaskIds = new();
        foreach (RuntimeExecutionTask task in tasks)
        {
            if (!ShouldRenderTask(task, revealHiddenTasks))
            {
                continue;
            }

            visibleTaskIds.Add(task.Id);
            RuntimeExecutionTaskId? visibleParentId = FindVisibleParentId(task, rawTasksById, revealHiddenTasks);
            parentByTaskId[task.Id] = visibleParentId;
            if (visibleParentId is not RuntimeExecutionTaskId resolvedVisibleParentId)
            {
                rootTaskIds.Add(task.Id);
                continue;
            }

            if (!childrenByParentId.TryGetValue(resolvedVisibleParentId, out List<RuntimeExecutionTaskId>? childIds))
            {
                childIds = new List<RuntimeExecutionTaskId>();
                childrenByParentId[resolvedVisibleParentId] = childIds;
            }

            childIds.Add(task.Id);
        }

        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> visibleTaskIdByRawTaskId = new();
        Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> ownedRawTaskIdsByVisibleNodeId = new();
        HashSet<RuntimeExecutionTaskId> visibleTaskIdSet = visibleTaskIds.ToHashSet();
        foreach (RuntimeExecutionTask rootTask in tasks.Where(task => task.ParentId == null))
        {
            MapRawTasksToVisibleOwners(
                rootTask.Id,
                currentVisibleOwnerId: null,
                visibleTaskIdSet,
                rawChildrenByParentId,
                visibleTaskIdByRawTaskId,
                ownedRawTaskIdsByVisibleNodeId);
        }

        return new ExecutionGraphProjection(
            visibleTaskIds,
            rootTaskIds,
            rawTasksById,
            childrenByParentId,
            parentByTaskId,
            visibleTaskIdByRawTaskId,
            ownedRawTaskIdsByVisibleNodeId);
    }

    /// <summary>
    /// Gets the visible node ids in deterministic source-task order.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> VisibleTaskIds { get; }

    /// <summary>
    /// Gets the visible root node ids.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> RootTaskIds { get; }

    /// <summary>
    /// Returns whether the provided task id is currently visible in the projected graph.
    /// </summary>
    public bool HasVisibleTask(RuntimeExecutionTaskId taskId)
    {
        return _visibleTaskIds.Contains(taskId);
    }

    /// <summary>
    /// Returns the raw task for the provided visible task id.
    /// </summary>
    public RuntimeExecutionTask GetRawTask(RuntimeExecutionTaskId taskId)
    {
        return _rawTasksById.TryGetValue(taskId, out RuntimeExecutionTask? task)
            ? task
            : throw new InvalidOperationException($"No raw execution task exists for '{taskId}'.");
    }

    /// <summary>
    /// Returns the direct visible child ids for one visible parent or for the root level when the parent id is null.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GetDirectChildIds(RuntimeExecutionTaskId? parentId)
    {
        if (parentId == null)
        {
            return RootTaskIds;
        }

        return _childrenByParentId.TryGetValue(parentId.Value, out List<RuntimeExecutionTaskId>? childIds)
            ? childIds
            : Array.Empty<RuntimeExecutionTaskId>();
    }

    /// <summary>
    /// Returns whether the provided visible task currently owns any direct visible child nodes.
    /// </summary>
    public bool HasChildren(RuntimeExecutionTaskId taskId)
    {
        return _childrenByParentId.TryGetValue(taskId, out List<RuntimeExecutionTaskId>? childIds) && childIds.Count > 0;
    }

    /// <summary>
    /// Returns every descendant visible leaf task id beneath the provided visible group id.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GetLeafDescendantIds(RuntimeExecutionTaskId groupId)
    {
        List<RuntimeExecutionTaskId> leafIds = new();
        CollectLeafDescendantIds(groupId, leafIds);
        return leafIds;
    }

    /// <summary>
    /// Returns the visible nodes that this visible node effectively depends on after descendant raw-task dependencies are
    /// rolled up to visible owners. Dependencies that remain inside the same visible subtree are excluded because they do
    /// not affect sibling staging or inter-node edges outside that subtree.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GetVisibleDependencyIds(RuntimeExecutionTaskId visibleNodeId)
    {
        HashSet<RuntimeExecutionTaskId> dependencyIds = new();
        foreach (RuntimeExecutionTaskId rawTaskId in EnumerateOwnedTaskSubtreeIds(visibleNodeId))
        {
            RuntimeExecutionTask rawTask = GetRawTask(rawTaskId);
            foreach (RuntimeExecutionTaskId dependencyId in rawTask.Dependencies)
            {
                RuntimeExecutionTaskId? visibleDependencyId = ResolveVisibleTaskId(dependencyId);
                if (visibleDependencyId is not RuntimeExecutionTaskId resolvedVisibleDependencyId)
                {
                    continue;
                }

                /* Rolled-up dependencies should only represent constraints outside the current visible subtree. When one
                   descendant depends on another descendant that is still rendered inside the same container, containment
                   already communicates that relationship and the parent container must not look externally blocked by its
                   own children. */
                if (resolvedVisibleDependencyId == visibleNodeId || IsVisibleAncestor(visibleNodeId, resolvedVisibleDependencyId))
                {
                    continue;
                }

                dependencyIds.Add(resolvedVisibleDependencyId);
            }
        }

        return dependencyIds.ToList();
    }

    /// <summary>
    /// Enumerates the visible node ids in the provided visible subtree, including the root container itself. Layout uses
    /// this to remap rolled-up descendant dependencies back to the direct visible sibling container that owns them.
    /// </summary>
    public IEnumerable<RuntimeExecutionTaskId> EnumerateVisibleSubtreeNodeIds(RuntimeExecutionTaskId rootId)
    {
        yield return rootId;
        foreach (RuntimeExecutionTaskId childId in GetDirectChildIds(rootId))
        {
            foreach (RuntimeExecutionTaskId descendantId in EnumerateVisibleSubtreeNodeIds(childId))
            {
                yield return descendantId;
            }
        }
    }

    /// <summary>
    /// Resolves one raw task id to the visible node that currently represents it in the collapsed graph.
    /// </summary>
    public RuntimeExecutionTaskId? ResolveVisibleTaskId(RuntimeExecutionTaskId rawTaskId)
    {
        return _visibleTaskIdByRawTaskId.TryGetValue(rawTaskId, out RuntimeExecutionTaskId visibleTaskId)
            ? visibleTaskId
            : null;
    }

    /// <summary>
    /// Returns whether one visible task currently sits in the ancestor chain of another visible task.
    /// </summary>
    public bool IsVisibleAncestor(RuntimeExecutionTaskId ancestorId, RuntimeExecutionTaskId descendantId)
    {
        foreach (RuntimeExecutionTaskId resolvedParentId in GetVisibleAncestorIds(descendantId))
        {
            if (resolvedParentId == ancestorId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the cached visible ancestor chain for one visible node id ordered from the direct parent upward.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GetVisibleAncestorIds(RuntimeExecutionTaskId nodeId)
    {
        return _visibleAncestorIdsByTaskId.TryGetValue(nodeId, out RuntimeExecutionTaskId[]? ancestorIds)
            ? ancestorIds
            : Array.Empty<RuntimeExecutionTaskId>();
    }

    /// <summary>
    /// Returns the visible hierarchy depth for the provided visible node id.
    /// </summary>
    public int GetVisibleDepth(RuntimeExecutionTaskId nodeId)
    {
        return _visibleAncestorIdsByTaskId.TryGetValue(nodeId, out RuntimeExecutionTaskId[]? ancestorIds)
            ? ancestorIds.Length
            : 0;
    }

    /// <summary>
    /// Restores selection to the nearest visible ancestor when a previously selected hidden task becomes collapsed.
    /// </summary>
    public RuntimeExecutionTaskId? ResolveVisibleSelectionId(RuntimeExecutionTaskId taskId)
    {
        if (HasVisibleTask(taskId))
        {
            return taskId;
        }

        RuntimeExecutionTaskId? currentTaskId = taskId;
        while (currentTaskId is RuntimeExecutionTaskId resolvedTaskId && _rawTasksById.TryGetValue(resolvedTaskId, out RuntimeExecutionTask? task))
        {
            if (HasVisibleTask(resolvedTaskId))
            {
                return resolvedTaskId;
            }

            currentTaskId = task.ParentId;
        }

        return null;
    }

    /// <summary>
    /// Returns the selected task id plus every descendant raw task id owned by that visible subtree.
    /// </summary>
    public IReadOnlyList<RuntimeExecutionTaskId> GetTaskSubtreeIds(RuntimeExecutionTaskId rootId)
    {
        return EnumerateOwnedTaskSubtreeIds(rootId).ToList();
    }

    /// <summary>
    /// Creates one immutable projection instance.
    /// </summary>
    private ExecutionGraphProjection(
        IReadOnlyList<RuntimeExecutionTaskId> visibleTaskIds,
        IReadOnlyList<RuntimeExecutionTaskId> rootTaskIds,
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTask> rawTasksById,
        Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> childrenByParentId,
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId?> parentByTaskId,
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> visibleTaskIdByRawTaskId,
        Dictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> ownedRawTaskIdsByVisibleNodeId)
    {
        VisibleTaskIds = visibleTaskIds;
        RootTaskIds = rootTaskIds;
        _visibleTaskIds = visibleTaskIds.ToHashSet();
        _rawTasksById = rawTasksById;
        _childrenByParentId = childrenByParentId;
        _visibleTaskIdByRawTaskId = visibleTaskIdByRawTaskId;
        _ownedRawTaskIdsByVisibleNodeId = ownedRawTaskIdsByVisibleNodeId;
        _visibleAncestorIdsByTaskId = BuildVisibleAncestorIdsByTaskId(visibleTaskIds, parentByTaskId);
    }

    /// <summary>
    /// Collects all descendant visible leaf ids for one group recursively.
    /// </summary>
    private void CollectLeafDescendantIds(RuntimeExecutionTaskId groupId, List<RuntimeExecutionTaskId> leafIds)
    {
        foreach (RuntimeExecutionTaskId childId in GetDirectChildIds(groupId))
        {
            if (!HasChildren(childId))
            {
                leafIds.Add(childId);
                continue;
            }

            CollectLeafDescendantIds(childId, leafIds);
        }
    }

    /// <summary>
    /// Enumerates one visible subtree's owned raw ids without exposing the internal ownership dictionary directly.
    /// </summary>
    private IEnumerable<RuntimeExecutionTaskId> EnumerateOwnedTaskSubtreeIds(RuntimeExecutionTaskId rootId)
    {
        if (_ownedRawTaskIdsByVisibleNodeId.TryGetValue(rootId, out List<RuntimeExecutionTaskId>? ownedTaskIds))
        {
            foreach (RuntimeExecutionTaskId taskId in ownedTaskIds)
            {
                yield return taskId;
            }
        }

        foreach (RuntimeExecutionTaskId childId in GetDirectChildIds(rootId))
        {
            foreach (RuntimeExecutionTaskId descendantId in EnumerateOwnedTaskSubtreeIds(childId))
            {
                yield return descendantId;
            }
        }
    }

    /// <summary>
    /// Builds the cached visible ancestor arrays for every visible node in the current projection snapshot.
    /// </summary>
    private static Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId[]> BuildVisibleAncestorIdsByTaskId(
        IReadOnlyList<RuntimeExecutionTaskId> visibleTaskIds,
        IReadOnlyDictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId?> parentByTaskId)
    {
        Dictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId[]> visibleAncestorIdsByTaskId = new(visibleTaskIds.Count);
        foreach (RuntimeExecutionTaskId nodeId in visibleTaskIds)
        {
            CacheVisibleAncestorIds(nodeId, parentByTaskId, visibleAncestorIdsByTaskId);
        }

        return visibleAncestorIdsByTaskId;
    }

    /// <summary>
    /// Materializes one cached visible ancestor array ordered from the direct parent upward.
    /// </summary>
    private static RuntimeExecutionTaskId[] CacheVisibleAncestorIds(
        RuntimeExecutionTaskId nodeId,
        IReadOnlyDictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId?> parentByTaskId,
        IDictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId[]> visibleAncestorIdsByTaskId)
    {
        if (visibleAncestorIdsByTaskId.TryGetValue(nodeId, out RuntimeExecutionTaskId[]? cachedAncestorIds))
        {
            return cachedAncestorIds;
        }

        RuntimeExecutionTaskId? currentParentId = parentByTaskId.TryGetValue(nodeId, out RuntimeExecutionTaskId? parentId)
            ? parentId
            : null;
        if (currentParentId is not RuntimeExecutionTaskId visibleParentId)
        {
            visibleAncestorIdsByTaskId[nodeId] = Array.Empty<RuntimeExecutionTaskId>();
            return visibleAncestorIdsByTaskId[nodeId];
        }

        RuntimeExecutionTaskId[] parentAncestorIds = CacheVisibleAncestorIds(visibleParentId, parentByTaskId, visibleAncestorIdsByTaskId);
        RuntimeExecutionTaskId[] ancestorIds = new RuntimeExecutionTaskId[parentAncestorIds.Length + 1];
        ancestorIds[0] = visibleParentId;
        Array.Copy(parentAncestorIds, 0, ancestorIds, 1, parentAncestorIds.Length);
        visibleAncestorIdsByTaskId[nodeId] = ancestorIds;
        return ancestorIds;
    }

    /// <summary>
    /// Climbs raw ancestry until the nearest visible parent is found.
    /// </summary>
    private static RuntimeExecutionTaskId? FindVisibleParentId(
        RuntimeExecutionTask task,
        IReadOnlyDictionary<RuntimeExecutionTaskId, RuntimeExecutionTask> rawTasksById,
        bool revealHiddenTasks)
    {
        RuntimeExecutionTaskId? currentParentId = task.ParentId;
        while (currentParentId is RuntimeExecutionTaskId parentId)
        {
            if (rawTasksById.TryGetValue(parentId, out RuntimeExecutionTask? parentTask) && ShouldRenderTask(parentTask, revealHiddenTasks))
            {
                return parentId;
            }

            currentParentId = rawTasksById.TryGetValue(parentId, out parentTask)
                ? parentTask.ParentId
                : null;
        }

        return null;
    }

    /// <summary>
    /// Maps every raw task to the visible node that owns it in the collapsed graph.
    /// </summary>
    private static void MapRawTasksToVisibleOwners(
        RuntimeExecutionTaskId taskId,
        RuntimeExecutionTaskId? currentVisibleOwnerId,
        IReadOnlySet<RuntimeExecutionTaskId> visibleTaskIds,
        IReadOnlyDictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> rawChildrenByParentId,
        IDictionary<RuntimeExecutionTaskId, RuntimeExecutionTaskId> visibleTaskIdByRawTaskId,
        IDictionary<RuntimeExecutionTaskId, List<RuntimeExecutionTaskId>> ownedRawTaskIdsByVisibleNodeId)
    {
        bool isVisible = visibleTaskIds.Contains(taskId);
        RuntimeExecutionTaskId? nextVisibleOwnerId = isVisible ? taskId : currentVisibleOwnerId;
        if (nextVisibleOwnerId is RuntimeExecutionTaskId visibleOwnerId)
        {
            visibleTaskIdByRawTaskId[taskId] = visibleOwnerId;
            if (!ownedRawTaskIdsByVisibleNodeId.TryGetValue(visibleOwnerId, out List<RuntimeExecutionTaskId>? ownedTaskIds))
            {
                ownedTaskIds = new List<RuntimeExecutionTaskId>();
                ownedRawTaskIdsByVisibleNodeId[visibleOwnerId] = ownedTaskIds;
            }

            ownedTaskIds.Add(taskId);
        }

        if (!rawChildrenByParentId.TryGetValue(taskId, out List<RuntimeExecutionTaskId>? childIds))
        {
            return;
        }

        foreach (RuntimeExecutionTaskId childId in childIds)
        {
            MapRawTasksToVisibleOwners(childId, nextVisibleOwnerId, visibleTaskIds, rawChildrenByParentId, visibleTaskIdByRawTaskId, ownedRawTaskIdsByVisibleNodeId);
        }
    }

    /// <summary>
    /// Returns whether one raw task should appear as a visible node.
    /// </summary>
    private static bool ShouldRenderTask(RuntimeExecutionTask task, bool revealHiddenTasks)
    {
        return revealHiddenTasks || !task.IsHiddenInGraph;
    }
}
