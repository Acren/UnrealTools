using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds a previewable execution plan by authoring directly against execution tasks and wiring sequencing dependencies
/// immediately as each child, body, or child operation is declared.
/// </summary>
public sealed class ExecutionPlanBuilder
{
    private readonly ExecutionPlanId _planId;
    private readonly string _title;
    private readonly List<ExecutionTask> _items = new();
    private readonly Dictionary<ExecutionTaskId, ParentBuildState> _parentStateByTaskId = new();
    private readonly Func<Operation, OperationParameters, ExecutionPlan?> _buildChildPlan;
    private IReadOnlyCollection<Type> _declaredOptionTypes = Array.Empty<Type>();
    private Operation _operation = null!;

    internal OperationParameters OperationParameters { get; private set; } = null!;

    /// <summary>
    /// Creates a builder for one composite execution plan.
    /// </summary>
    public ExecutionPlanBuilder(string title, ExecutionPlanId? planId = null, Func<Operation, OperationParameters, ExecutionPlan?>? buildChildPlan = null)
    {
        _title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution plan title is required.", nameof(title))
            : title;
        _planId = planId ?? ExecutionIdentifierFactory.CreatePlanId(title);
        _buildChildPlan = buildChildPlan ?? ExecutionPlanFactory.BuildPlan;
    }

    /// <summary>
    /// Declares one authored task container in the plan and returns its fluent builder. Exactly one root task is allowed;
    /// additional tasks must be declared beneath that root.
    /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null, ExecutionTaskId? parentId = null)
    {
        ValidateParent(parentId);
        ExecutionTask task = CreateItem(GenerateTaskId(), title, description, parentId);
        AddTaskDefinition(task);
        return new ExecutionTaskBuilder(this, task, parentId);
    }

    /// <summary>
    /// Root-level sibling scopes are not supported because plans still require one real root container task.
    /// </summary>
    public void Children(Action<ExecutionTaskScopeBuilder> build)
    {
        _ = build ?? throw new ArgumentNullException(nameof(build));
        throw new InvalidOperationException("Execution plans require exactly one root task. Declare the root with Task(...), then add children from that root task.");
    }

    /// <summary>
    /// Builds the authored execution plan used by both preview and runtime scheduling.
    /// </summary>
    public ExecutionPlan BuildPlan()
    {
        List<ExecutionTask> tasks = _items.ToList();
        List<ExecutionDependency> dependencies = _items
            .SelectMany(item => item.DependsOn.Select(dependencyId => new ExecutionDependency(dependencyId, item.Id)))
            .ToList();
        return new ExecutionPlan(_planId, _title, tasks, dependencies);
    }

    internal void SetBuilderOperationParameters(OperationParameters operationParameters)
    {
        OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
    }

    internal void SetOperation(Operation operation)
    {
        _operation = operation ?? throw new ArgumentNullException(nameof(operation));
    }

    internal void SetDeclaredOptionTypes(IEnumerable<Type> declaredOptionTypes)
    {
        _declaredOptionTypes = (declaredOptionTypes ?? throw new ArgumentNullException(nameof(declaredOptionTypes))).ToList();
    }

    /// <summary>
    /// Expands one dependency on a previously declared task into dependencies on the current completion frontier of that
    /// task subtree so explicit After(...) calls still follow authored child and body sequencing.
    /// </summary>
    internal void AddTaskDependency(ExecutionTask task, ExecutionTaskId dependencyId)
    {
        task.AddDependency(dependencyId);
    }

    /// <summary>
    /// Updates whether one authored task participates in the plan and records the disabled reason when it does not.
    /// </summary>
    internal void SetCondition(ExecutionTask task, bool enabled, string? disabledReason)
    {
        task.SetCondition(enabled, disabledReason);
    }

    /// <summary>
    /// Updates the authored description of one task.
    /// </summary>
    internal void SetDescription(ExecutionTask task, string? description)
    {
        task.SetDescription(description ?? string.Empty);
    }

    /// <summary>
    /// Updates whether one authored task is hidden in the graph projection.
    /// </summary>
    internal void SetGraphVisibility(ExecutionTask task, bool isHiddenInGraph)
    {
        task.SetHiddenInGraph(isHiddenInGraph);
    }

    /// <summary>
    /// Declares one sequential child task beneath the provided parent and immediately advances that parent's completion
    /// frontier to the newly authored child.
    /// </summary>
    internal ExecutionTaskBuilder DeclareSequentialRelativeTask(ExecutionTaskId parentId, string title, string? description)
    {
        ParentBuildState parentState = GetParentState(parentId);
        ExecutionTaskBuilder childTask = CreateRelativeTask(parentId, title, description, parentState.CompletionFrontier, parentState.CompletionFrontier);
        ReplaceFrontier(parentState.CompletionFrontier, childTask.Id);
        return childTask;
    }

    /// <summary>
    /// Declares one sequential task inside an active child scope by depending on that scope's current completion frontier
    /// and then replacing the scope frontier with the newly authored child.
    /// </summary>
    internal ExecutionTaskBuilder DeclareScopedSequentialRelativeTask(ExecutionTaskId parentId, string title, string? description, IList<ExecutionTaskId> scopeFrontier)
    {
        ExecutionTaskBuilder childTask = CreateRelativeTask(parentId, title, description, scopeFrontier.ToList(), scopeFrontier);
        ReplaceFrontier(scopeFrontier, childTask.Id);
        return childTask;
    }

    /// <summary>
    /// Declares one parallel child task inside an active child scope by depending on the incoming scope frontier while
    /// leaving sibling tasks independent from each other.
    /// </summary>
    internal ExecutionTaskBuilder DeclareScopedParallelRelativeTask(ExecutionTaskId parentId, string title, string? description, IReadOnlyList<ExecutionTaskId> incomingFrontier, IList<ExecutionTaskId> scopeFrontier)
    {
        ExecutionTaskBuilder childTask = CreateRelativeTask(parentId, title, description, incomingFrontier);
        AddUnique(scopeFrontier, childTask.Id);
        return childTask;
    }

    /// <summary>
    /// Declares the next fluent sibling after the provided task by depending on that visible authored task directly.
    /// Runtime completion rollup keeps authored-task dependencies waiting until the full subtree finishes.
    /// </summary>
    internal ExecutionTaskBuilder DeclareNextSiblingTask(ExecutionTaskId previousTaskId, ExecutionTaskId parentId, string title, string? description, IList<ExecutionTaskId>? lastTaskIds)
    {
        IList<ExecutionTaskId> activeLastTaskIds = lastTaskIds ?? GetParentState(parentId).CompletionFrontier;
        ExecutionTaskBuilder siblingTask = CreateRelativeTask(parentId, title, description, new[] { previousTaskId }, activeLastTaskIds);
        ReplaceFrontier(activeLastTaskIds, siblingTask.Id);
        return siblingTask;
    }

    /// <summary>
    /// Opens one child-task scope beneath the provided parent and lets the scope own the transient frontier state needed
    /// to model sequential or parallel sibling authoring.
    /// </summary>
    internal void BuildChildScope(ExecutionTask parentTask, ExecutionChildMode mode, Action<ExecutionTaskScopeBuilder> build)
    {
        _ = parentTask ?? throw new ArgumentNullException(nameof(parentTask));
        _ = build ?? throw new ArgumentNullException(nameof(build));

        ParentBuildState parentState = GetParentState(parentTask.Id);
        ExecutionTaskScopeBuilder scopeBuilder = new(this, parentTask.Id, mode, parentState.CompletionFrontier);
        build(scopeBuilder);
        ReplaceFrontier(parentState.CompletionFrontier, scopeBuilder.CompletionFrontier);
    }

    /// <summary>
    /// Attaches one hidden body task beneath the provided parent task and immediately advances the parent's completion
    /// frontier to that body task.
    /// </summary>
    internal ExecutionTaskId AttachBodyTask(ExecutionTask parentTask, Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        _ = parentTask ?? throw new ArgumentNullException(nameof(parentTask));
        Func<ExecutionTaskContext, Task<OperationResult>> resolvedExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));

        ParentBuildState parentState = GetParentState(parentTask.Id);
        parentState.BodyCount += 1;

        ExecutionTask bodyTask = new ExecutionTask(
            GenerateTaskId(),
            CreateBodyTaskTitle(parentTask, parentState.BodyCount),
            parentTask.Operation,
            parentTask.Description,
            parentTask.Id,
            enabled: parentTask.Enabled,
            disabledReason: parentTask.DisabledReason,
            operationParameters: parentTask.OperationParameters,
            declaredOptionTypes: parentTask.DeclaredOptionTypes,
            executeAsync: resolvedExecuteAsync,
            outcome: null,
            isOperationRoot: false,
            isHiddenInGraph: true);

        AddTaskDefinition(bodyTask);
        WireDependencies(bodyTask, parentState.CompletionFrontier);
        ReplaceFrontier(parentState.CompletionFrontier, bodyTask.Id);
        return bodyTask.Id;
    }

    /// <summary>
    /// Expands one child operation immediately, attaches the imported root beneath the current parent, and advances the
    /// parent's completion frontier to the imported subtree's leaf tasks.
    /// </summary>
    internal ExecutionChildOperationBuilder AttachChildOperation(ExecutionTask parentTask, Type operationType, Func<OperationParameters> createParameters, string title, string? description)
    {
        _ = parentTask ?? throw new ArgumentNullException(nameof(parentTask));
        Type resolvedOperationType = operationType ?? throw new ArgumentNullException(nameof(operationType));
        Func<OperationParameters> createChildParameters = createParameters ?? throw new ArgumentNullException(nameof(createParameters));

        Operation childOperation = Operation.CreateOperation(resolvedOperationType);
        OperationParameters childParameters = createChildParameters();
        ExecutionPlan childPlan = _buildChildPlan(childOperation, childParameters)
            ?? throw new InvalidOperationException($"Child operation '{resolvedOperationType.Name}' returned no execution plan during authoring.");
        InsertedExecutionTasks insertedTasks = ExecutionTaskInsertion.InsertUnderParent(
            childPlan,
            parentTask.Id,
            new ChildOperationRootOverrides(title, description ?? string.Empty, isHiddenInGraph: false));

        foreach (ExecutionTask childTask in insertedTasks.Tasks)
        {
            AddImportedDefinition(childTask);
        }

        ParentBuildState parentState = GetParentState(parentTask.Id);
        ExecutionTask importedRootTask = GetDefinition(insertedTasks.RootTaskId);
        WireDependencies(importedRootTask, parentState.CompletionFrontier);
        ReplaceFrontier(parentState.CompletionFrontier, GetLeafCompletionTaskIds(insertedTasks.Tasks));
        return new ExecutionChildOperationBuilder(this, importedRootTask);
    }

    /// <summary>
    /// Assigns the current validated operation parameters to one task while it is still only authored plan data.
    /// </summary>
    internal void SetOperationParameters(ExecutionTask task, OperationParameters operationParameters)
    {
        task.SetOperationParameters(operationParameters ?? throw new ArgumentNullException(nameof(operationParameters)), _declaredOptionTypes);
    }

    /// <summary>
    /// Creates one direct child task and wires it to the provided authored dependency frontier.
    /// </summary>
    private ExecutionTaskBuilder CreateRelativeTask(ExecutionTaskId parentId, string title, string? description, IReadOnlyList<ExecutionTaskId> dependencyFrontier, IList<ExecutionTaskId>? lastTaskIds = null)
    {
        ExecutionTask task = CreateItem(GenerateTaskId(), title, description, parentId);
        AddTaskDefinition(task);
        WireDependencies(task, dependencyFrontier);
        return new ExecutionTaskBuilder(this, task, parentId, lastTaskIds);
    }

    /// <summary>
    /// Fails loudly when more than one root task is authored in one execution plan.
    /// </summary>
    private void ValidateParent(ExecutionTaskId? parentId)
    {
        if (parentId != null)
        {
            return;
        }

        if (_items.Any(item => item.ParentId == null))
        {
            throw new InvalidOperationException("Execution plans require exactly one root task. Add new tasks beneath the existing root task instead of creating another root.");
        }
    }

    /// <summary>
    /// Creates one authored task with the current builder-wide operation context already attached.
    /// </summary>
    private ExecutionTask CreateItem(ExecutionTaskId id, string title, string? description, ExecutionTaskId? parentId)
    {
        return new ExecutionTask(
            id: id,
            title: string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("Execution item title is required.", nameof(title)) : title,
            operation: _operation,
            description: description ?? string.Empty,
            parentId: parentId,
            enabled: true,
            disabledReason: string.Empty,
            operationParameters: OperationParameters,
            declaredOptionTypes: _declaredOptionTypes,
            outcome: null,
            isOperationRoot: parentId == null,
            isHiddenInGraph: false);
    }

    /// <summary>
    /// Registers one imported task definition and creates the matching builder-side tracking state.
    /// </summary>
    private void AddImportedDefinition(ExecutionTask task)
    {
        _ = task ?? throw new ArgumentNullException(nameof(task));
        if (_items.Any(item => item.Id == task.Id))
        {
            throw new InvalidOperationException($"Execution plan already contains task '{task.Id}'. Imported child-operation task ids must be globally unique.");
        }

        AddTaskDefinition(task);
    }

    /// <summary>
    /// Returns one task definition from the builder-owned task list.
    /// </summary>
    private ExecutionTask GetDefinition(ExecutionTaskId taskId)
    {
        return _items.Single(item => item.Id == taskId);
    }

    /// <summary>
    /// Adds dependency edges from every task in the provided frontier to the newly declared task.
    /// </summary>
    private static void WireDependencies(ExecutionTask task, IReadOnlyList<ExecutionTaskId> dependencyFrontier)
    {
        foreach (ExecutionTaskId dependencyTaskId in dependencyFrontier)
        {
            task.AddDependency(dependencyTaskId);
        }
    }

    /// <summary>
    /// Replaces one mutable frontier list with a new ordered set of completion task ids.
    /// </summary>
    private static void ReplaceFrontier(IList<ExecutionTaskId> target, IReadOnlyList<ExecutionTaskId> source)
    {
        target.Clear();
        foreach (ExecutionTaskId taskId in source)
        {
            AddUnique(target, taskId);
        }
    }

    /// <summary>
    /// Replaces one mutable frontier list with one single completion task id.
    /// </summary>
    private static void ReplaceFrontier(IList<ExecutionTaskId> target, ExecutionTaskId taskId)
    {
        target.Clear();
        target.Add(taskId);
    }

    /// <summary>
    /// Adds one completion task id only when it is not already present.
    /// </summary>
    private static void AddUnique(ICollection<ExecutionTaskId> target, ExecutionTaskId taskId)
    {
        if (!target.Contains(taskId))
        {
            target.Add(taskId);
        }
    }

    /// <summary>
    /// Generates the stable body-task title sequence shown in the graph for repeated Run(...) calls on one parent task.
    /// </summary>
    private static string CreateBodyTaskTitle(ExecutionTask parentTask, int bodyIndex)
    {
        return bodyIndex == 1
            ? parentTask.Title + ".Body"
            : parentTask.Title + ".Body " + bodyIndex;
    }

    /// <summary>
    /// Returns the leaf tasks of one imported subtree so the parent's frontier advances to the tasks that actually finish
    /// the child operation step.
    /// </summary>
    private static IReadOnlyList<ExecutionTaskId> GetLeafCompletionTaskIds(IReadOnlyList<ExecutionTask> tasks)
    {
        HashSet<ExecutionTaskId> parentIds = tasks
            .Where(task => task.ParentId != null)
            .Select(task => task.ParentId!.Value)
            .ToHashSet();
        return tasks
            .Where(task => !parentIds.Contains(task.Id))
            .Select(task => task.Id)
            .ToList();
    }

    /// <summary>
    /// Registers one builder-owned task together with the per-parent authoring state used to track completion frontiers
    /// and body numbering.
    /// </summary>
    private void AddTaskDefinition(ExecutionTask task)
    {
        _items.Add(task);
        _parentStateByTaskId.Add(task.Id, new ParentBuildState());
    }

    /// <summary>
    /// Returns the builder-owned state for one authored task.
    /// </summary>
    private ParentBuildState GetParentState(ExecutionTaskId taskId)
    {
        return _parentStateByTaskId[taskId];
    }

    /// <summary>
    /// Generates a globally unique task identifier so embedded child plans can keep their authored ids without any
    /// parent-side remapping during plan composition or live runtime insertion.
    /// </summary>
    private ExecutionTaskId GenerateTaskId()
    {
        return ExecutionTaskId.New();
    }

    /// <summary>
    /// Tracks the current completion frontier and body naming count for one parent task while its nested child and body
    /// steps are still being declared.
    /// </summary>
    private sealed class ParentBuildState
    {
        public List<ExecutionTaskId> CompletionFrontier { get; } = new();

        public int BodyCount { get; set; }
    }
}

/// <summary>
/// Inserts one execution plan beneath an existing parent task by preserving the child plan's authored task ids and only
/// patching the imported root so both build-time expansion and live runtime insertion share the same direct-attach model.
/// </summary>
internal static class ExecutionTaskInsertion
{
    /// <summary>
    /// Inserts one child plan beneath an existing parent task by attaching the child root directly under that parent while
    /// preserving every authored task id and internal relationship inside the imported subtree.
    /// </summary>
    public static InsertedExecutionTasks InsertUnderParent(ExecutionPlan childPlan, ExecutionTaskId parentTaskId, ChildOperationRootOverrides? rootOverrides = null)
    {
        if (childPlan == null)
        {
            throw new ArgumentNullException(nameof(childPlan));
        }

        IReadOnlyList<ExecutionTask> orderedTasks = childPlan.Tasks.ToList();
        ExecutionTask childRoot = orderedTasks.Single(task => task.ParentId == null);
        List<ExecutionTask> insertedTasks = new(orderedTasks.Count);
        foreach (ExecutionTask childTask in orderedTasks)
        {
            bool isImportedRoot = childTask.Id == childRoot.Id;
            insertedTasks.Add(CreateImportedTask(childTask, isImportedRoot ? parentTaskId : childTask.ParentId, isImportedRoot ? rootOverrides : null));
        }

        return new InsertedExecutionTasks(insertedTasks, childRoot.Id);
    }

    /// <summary>
    /// Creates one imported task instance while preserving the authored task id and internal dependency structure from the
    /// child plan. Only the imported root receives a new parent link and optional parent-side presentation overrides.
    /// </summary>
    private static ExecutionTask CreateImportedTask(ExecutionTask childTask, ExecutionTaskId? parentId, ChildOperationRootOverrides? rootOverrides)
    {
        return childTask.CreateClone(
            parentId,
            rootOverrides?.Title ?? childTask.Title,
            rootOverrides?.Description ?? childTask.Description,
            childTask.IsHiddenInGraph || (rootOverrides?.IsHiddenInGraph ?? false),
            outcome: null);
    }
}

internal sealed class ChildOperationRootOverrides
{
    public ChildOperationRootOverrides(string title, string description, bool isHiddenInGraph)
    {
        Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("Child operation root title is required.", nameof(title)) : title;
        Description = description ?? string.Empty;
        IsHiddenInGraph = isHiddenInGraph;
    }

    public string Title { get; }

    public string Description { get; set; }

    public bool IsHiddenInGraph { get; set; }
}

/// <summary>
/// Carries the task definitions produced by inserting a child plan beneath an existing parent task.
/// </summary>
internal sealed class InsertedExecutionTasks
{
    public InsertedExecutionTasks(IReadOnlyList<ExecutionTask> tasks, ExecutionTaskId rootTaskId)
    {
        Tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        RootTaskId = rootTaskId;
    }

    public IReadOnlyList<ExecutionTask> Tasks { get; }

    public ExecutionTaskId RootTaskId { get; }
}
