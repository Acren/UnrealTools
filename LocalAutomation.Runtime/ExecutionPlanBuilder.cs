using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds a previewable execution plan where ordered body steps, child tasks, parallel child groups, and expanded child
/// operations all participate in one explicit step list beneath each parent task.
/// </summary>
public sealed class ExecutionPlanBuilder
{
    private readonly ExecutionPlanId _planId;
    private readonly string _title;
    private readonly List<ExecutionTask> _items = new();
    private readonly Dictionary<ExecutionTaskId, BuilderTaskState> _builderStateByTaskId = new();
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
        _buildChildPlan = buildChildPlan ?? Runner.BuildPlan;
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
        ExpandChildOperations();
        ApplyChildDeclarationOrdering();
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

    internal void AddTaskDependency(ExecutionTask task, ExecutionTaskId dependencyId)
    {
        foreach (ExecutionTaskId completionTaskId in GetCompletionTaskIds(dependencyId))
        {
            AddDirectDependency(task, completionTaskId);
        }
    }

    internal void SetCondition(ExecutionTask task, bool enabled, string? disabledReason)
    {
        task.SetCondition(enabled, disabledReason);
    }

    internal void SetDescription(ExecutionTask task, string? description)
    {
        task.SetDescription(description ?? string.Empty);
    }

    internal void SetGraphVisibility(ExecutionTask task, bool isHiddenInGraph)
    {
        task.SetHiddenInGraph(isHiddenInGraph);
    }

    /// <summary>
    /// Declares one task relative to an existing parent and appends it to the correct ordered step in that parent's child
    /// step list.
    /// </summary>
    internal ExecutionTaskBuilder DeclareRelativeTask(ExecutionTaskId parentId, string title, string? description, bool parallel, ref ChildDeclarationEntry? sharedDeclaration)
    {
        ExecutionTaskBuilder task = Task(title, description, parentId);

        /* Sequential declarations each get their own ordered step, while parallel scopes reuse one shared step whose
           entry and completion sets are the same sibling task list. */
        ChildDeclarationEntry declaration = parallel
            ? sharedDeclaration ??= AppendChildStep(parentId)
            : AppendChildStep(parentId);
        if (!parallel)
        {
            declaration.RootTaskId = task.Id;
        }

        declaration.AddCompletionTask(task.Id);

        return task;
    }

    internal ExecutionTaskId AttachBodyTask(ExecutionTask parentTask, Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        _ = parentTask ?? throw new ArgumentNullException(nameof(parentTask));
        Func<ExecutionTaskContext, Task<OperationResult>> resolvedExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));

        ExecutionTask bodyTask = CreateItem(
            GenerateTaskId(),
            CreateBodyTaskTitle(parentTask),
            parentTask.Description,
            parentTask.Id);
        bodyTask.SetCondition(parentTask.Enabled, parentTask.DisabledReason);
        bodyTask.SetOperationParameters(parentTask.OperationParameters, parentTask.DeclaredOptionTypes);
        bodyTask.SetExecuteAsync(resolvedExecuteAsync);
        bodyTask.SetOperationRoot(false);
        /* Internal body tasks carry the runnable work for authored Run(...) declarations, so the graph hides them by
           default and lets the global reveal toggle surface them only when lower-level troubleshooting is needed. */
        bodyTask.SetHiddenInGraph(true);
        AddTaskDefinition(bodyTask);

        ChildDeclarationEntry declaration = AppendChildStep(parentTask.Id, isBody: true);
        declaration.RootTaskId = bodyTask.Id;
        declaration.AddCompletionTask(bodyTask.Id);

        return bodyTask.Id;
    }

    /// <summary>
    /// Registers one child-operation declaration beneath the current task. The declaration stores parent-side overrides
    /// for the imported child root while the child operation itself still owns the subtree that will be attached later.
    /// </summary>
    internal ChildDeclarationEntry AttachChildOperation(ExecutionTask parentTask, Type operationType, Func<OperationParameters> createParameters, string title, string? description)
    {
        _ = parentTask ?? throw new ArgumentNullException(nameof(parentTask));
        ChildDeclarationEntry declaration = AppendChildStep(parentTask.Id);
        declaration.ChildOperationType = operationType ?? throw new ArgumentNullException(nameof(operationType));
        declaration.CreateChildParameters = createParameters ?? throw new ArgumentNullException(nameof(createParameters));
        declaration.RootOverrides = new ChildOperationRootOverrides(title, description ?? string.Empty, isHiddenInGraph: false);
        return declaration;
    }

    internal ExecutionTaskBuilder DeclareParallelRelativeTask(ExecutionTaskId parentId, string title, string? description, ref ChildDeclarationEntry? sharedDeclaration)
    {
        return DeclareRelativeTask(parentId, title, description, parallel: true, ref sharedDeclaration);
    }

    internal ExecutionTaskBuilder DeclareSequentialRelativeTask(ExecutionTaskId parentId, string title, string? description)
    {
        ChildDeclarationEntry? sharedDeclaration = null;
        return DeclareRelativeTask(parentId, title, description, parallel: false, ref sharedDeclaration);
    }

    /// <summary>
    /// Updates the parent-authored description override for one imported child-operation root.
    /// </summary>
    internal void SetChildOperationDescription(ChildDeclarationEntry declaration, string? description)
    {
        _ = declaration ?? throw new ArgumentNullException(nameof(declaration));
        declaration.RootOverrides.Description = description ?? string.Empty;
    }

    /// <summary>
    /// Updates whether the imported child-operation root should be hidden in the graph projection.
    /// </summary>
    internal void SetChildOperationGraphVisibility(ChildDeclarationEntry declaration, bool isHiddenInGraph)
    {
        _ = declaration ?? throw new ArgumentNullException(nameof(declaration));
        declaration.RootOverrides.IsHiddenInGraph = isHiddenInGraph;
    }

    internal void SetOperationParameters(ExecutionTask task, OperationParameters operationParameters)
    {
        task.SetOperationParameters(operationParameters ?? throw new ArgumentNullException(nameof(operationParameters)), _declaredOptionTypes);
    }

    internal IReadOnlyList<ExecutionTaskId> GetCompletionTaskIds(ExecutionTaskId taskId)
    {
        BuilderTaskState state = GetBuilderState(taskId);
        if (state.ChildDeclarations.Count == 0)
        {
            return new[] { taskId };
        }

        ChildDeclarationEntry? lastDeclaration = state.ChildDeclarations
            .LastOrDefault(entry => entry.CompletionTaskIds.Count > 0);
        return lastDeclaration?.CompletionTaskIds.Count > 0
            ? lastDeclaration.CompletionTaskIds.ToList()
            : new[] { taskId };
    }

    internal ChildDeclarationEntry AppendChildStep(ExecutionTaskId parentId, bool isBody = false)
    {
        BuilderTaskState state = GetBuilderState(parentId);
        ChildDeclarationEntry entry = new(isBody);
        state.ChildDeclarations.Add(entry);
        return entry;
    }

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
            executeAsync: null,
            outcome: null,
            isOperationRoot: parentId == null,
            isHiddenInGraph: false);
    }

    private void ExpandChildOperations()
    {
        List<(ExecutionTask Parent, ChildDeclarationEntry Declaration)> declarationsToExpand = _items
            .SelectMany(item => GetBuilderState(item.Id).ChildDeclarations.Select(declaration => (Parent: item, Declaration: declaration)))
            .Where(tuple => tuple.Declaration.ChildOperationType != null)
            .ToList();

        foreach ((ExecutionTask parentTask, ChildDeclarationEntry declaration) in declarationsToExpand)
        {
            Operation childOperation = Operation.CreateOperation(declaration.ChildOperationType!);
            OperationParameters childParameters = declaration.CreateChildParameters!();
            ExecutionPlan childPlan = _buildChildPlan(childOperation, childParameters)
                ?? throw new InvalidOperationException($"Child operation '{declaration.ChildOperationType!.Name}' returned no execution plan during expansion.");
            ChildOperationRootOverrides rootOverrides = new(
                declaration.RootOverrides.Title,
                declaration.RootOverrides.Description,
                declaration.RootOverrides.IsHiddenInGraph);
            InsertedExecutionTasks insertedTasks = ExecutionTaskInsertion.InsertUnderParent(childPlan, parentTask.Id, rootOverrides);

            declaration.ChildOperationType = null;
            declaration.CreateChildParameters = null;
            declaration.RootTaskId = insertedTasks.RootTaskId;
            declaration.CompletionTaskIds.Clear();
            declaration.CompletionTaskIds.AddRange(GetLeafCompletionTaskIds(insertedTasks.Tasks));

            foreach (ExecutionTask childTask in insertedTasks.Tasks)
            {
                AddImportedDefinition(childTask);
            }
        }
    }

    /// <summary>
    /// Adds one imported task definition to the current plan and fails loudly if a supposedly unique task id collides.
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

    private void ApplyChildDeclarationOrdering()
    {
        foreach (ExecutionTask task in _items)
        {
            List<ExecutionTaskId> previousCompletionTaskIds = new();

            /* Child declarations are append-only, so authored insertion order is already the execution-order source of
               truth. Keeping that order directly avoids carrying redundant sequence metadata just to sort it back later. */
            foreach (ChildDeclarationEntry declaration in GetBuilderState(task.Id).ChildDeclarations)
            {
                foreach (ExecutionTaskId entryTaskId in declaration.GetEntryTaskIds())
                {
                    ExecutionTask entryTask = GetDefinition(entryTaskId);
                    foreach (ExecutionTaskId previousCompletionTaskId in previousCompletionTaskIds)
                    {
                        AddDirectDependency(entryTask, previousCompletionTaskId);
                    }
                }

                if (declaration.CompletionTaskIds.Count > 0)
                {
                    previousCompletionTaskIds = declaration.CompletionTaskIds.ToList();
                }
            }
        }
    }

    private void AddDirectDependency(ExecutionTask task, ExecutionTaskId dependencyId)
    {
        task.AddDependency(dependencyId);
    }

    private ExecutionTask GetDefinition(ExecutionTaskId taskId)
    {
        return _items.Single(item => item.Id == taskId);
    }

    private string CreateBodyTaskTitle(ExecutionTask parentTask)
    {
        int bodyIndex = GetBuilderState(parentTask.Id).ChildDeclarations.Count(entry => entry.IsBody) + 1;
        return bodyIndex == 1
            ? parentTask.Title + ".Body"
            : parentTask.Title + ".Body " + bodyIndex;
    }

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
    /// Registers one builder-owned task together with the sidecar declaration state used only during plan authoring.
    /// </summary>
    private void AddTaskDefinition(ExecutionTask task)
    {
        _items.Add(task);
        _builderStateByTaskId.Add(task.Id, new BuilderTaskState());
    }

    /// <summary>
    /// Returns the builder-only declaration state for one authored task.
    /// </summary>
    private BuilderTaskState GetBuilderState(ExecutionTaskId taskId)
    {
        return _builderStateByTaskId[taskId];
    }

    /// <summary>
     /// Generates a globally unique task identifier so embedded child plans can keep their authored ids without any
    /// parent-side remapping during plan composition or live runtime insertion.
    /// </summary>
    private ExecutionTaskId GenerateTaskId()
    {
        return ExecutionTaskId.New();
    }

    internal sealed class BuilderTaskState
    {
        public List<ChildDeclarationEntry> ChildDeclarations { get; } = new();
    }

    internal sealed class ChildDeclarationEntry
    {
        public ChildDeclarationEntry(bool isBody)
        {
            IsBody = isBody;
        }

        public bool IsBody { get; }

        public ExecutionTaskId? RootTaskId { get; set; }

        public List<ExecutionTaskId> CompletionTaskIds { get; } = new();

        public Type? ChildOperationType { get; set; }

        public Func<OperationParameters>? CreateChildParameters { get; set; }

        public ChildOperationRootOverrides RootOverrides { get; set; } = null!;

        public IEnumerable<ExecutionTaskId> GetEntryTaskIds()
        {
            if (RootTaskId is ExecutionTaskId rootTaskId)
            {
                yield return rootTaskId;
                yield break;
            }

            foreach (ExecutionTaskId completionTaskId in CompletionTaskIds)
            {
                yield return completionTaskId;
            }
        }

        public void AddCompletionTask(ExecutionTaskId taskId)
        {
            if (!CompletionTaskIds.Contains(taskId))
            {
                CompletionTaskIds.Add(taskId);
            }
        }
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
        return new ExecutionTask(
            childTask.Id,
            rootOverrides?.Title ?? childTask.Title,
            childTask.Operation,
            rootOverrides?.Description ?? childTask.Description,
            parentId,
            childTask.DependsOn,
            childTask.Enabled,
            childTask.DisabledReason,
            childTask.OperationParameters,
            childTask.DeclaredOptionTypes,
            childTask.ExecuteAsync,
            outcome: null,
            isOperationRoot: childTask.IsOperationRoot,
            isHiddenInGraph: childTask.IsHiddenInGraph || (rootOverrides?.IsHiddenInGraph ?? false));
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
