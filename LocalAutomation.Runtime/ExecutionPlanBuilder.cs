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
    private readonly List<PlanItemDefinition> _items = new();
    private readonly Func<Operation, OperationParameters, ExecutionPlan?> _buildChildPlan;
    private IReadOnlyCollection<Type> _declaredOptionTypes = Array.Empty<Type>();
    private int _nextSequence = 0;
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
        PlanItemDefinition definition = CreateItem(GenerateTaskId(), title, description, parentId);
        _items.Add(definition);
        return new ExecutionTaskBuilder(this, definition, parentId);
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
        List<ExecutionTask> tasks = _items
            .Select(item => new ExecutionTask(
                id: item.Id,
                title: item.Title,
                operation: item.Operation,
                description: item.Description,
                parentId: item.ParentId,
                dependsOn: item.DependencyIds,
                enabled: item.Enabled,
                disabledReason: item.DisabledReason,
                operationParameters: item.OperationParameters,
                declaredOptionTypes: item.DeclaredOptionTypes,
                executeAsync: item.ExecuteAsync,
                outcome: null,
                isOperationRoot: item.IsOperationRoot,
                isHiddenInGraph: item.IsHiddenInGraph))
            .ToList();
        List<ExecutionDependency> dependencies = _items
            .SelectMany(item => item.DependencyIds.Select(dependencyId => new ExecutionDependency(dependencyId, item.Id)))
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

    internal void AddTaskDependency(PlanItemDefinition definition, ExecutionTaskId dependencyId)
    {
        foreach (ExecutionTaskId completionTaskId in GetCompletionTaskIds(dependencyId))
        {
            AddDirectDependency(definition, completionTaskId);
        }
    }

    internal void SetCondition(PlanItemDefinition definition, bool enabled, string? disabledReason)
    {
        definition.Enabled = enabled;
        definition.DisabledReason = enabled ? string.Empty : (disabledReason ?? string.Empty);
    }

    internal void SetDescription(PlanItemDefinition definition, string? description)
    {
        definition.Description = description ?? string.Empty;
    }

    internal void SetGraphVisibility(PlanItemDefinition definition, bool isHiddenInGraph)
    {
        definition.IsHiddenInGraph = isHiddenInGraph;
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
            ? sharedDeclaration ??= AppendChildStep(parentId, ChildDeclarationKind.ParallelChildren)
            : AppendChildStep(parentId, ChildDeclarationKind.ChildTask);
        if (!parallel)
        {
            declaration.RootTaskId = task.Definition.Id;
        }

        declaration.AddCompletionTask(task.Definition.Id);

        return task;
    }

    internal ExecutionTaskId AttachBodyTask(PlanItemDefinition parentDefinition, Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        _ = parentDefinition ?? throw new ArgumentNullException(nameof(parentDefinition));
        Func<ExecutionTaskContext, Task<OperationResult>> resolvedExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));

        PlanItemDefinition bodyDefinition = CreateItem(
            GenerateTaskId(),
            CreateBodyTaskTitle(parentDefinition),
            parentDefinition.Description,
            parentDefinition.Id);
        bodyDefinition.Enabled = parentDefinition.Enabled;
        bodyDefinition.DisabledReason = parentDefinition.DisabledReason;
        bodyDefinition.OperationParameters = parentDefinition.OperationParameters;
        bodyDefinition.DeclaredOptionTypes = parentDefinition.DeclaredOptionTypes.ToList();
        bodyDefinition.ExecuteAsync = resolvedExecuteAsync;
        bodyDefinition.IsOperationRoot = false;
        /* Internal body tasks carry the runnable work for authored Run(...) declarations, so the graph hides them by
           default and lets the global reveal toggle surface them only when lower-level troubleshooting is needed. */
        bodyDefinition.IsHiddenInGraph = true;
        _items.Add(bodyDefinition);

        ChildDeclarationEntry declaration = AppendChildStep(parentDefinition.Id, ChildDeclarationKind.Body);
        declaration.RootTaskId = bodyDefinition.Id;
        declaration.AddCompletionTask(bodyDefinition.Id);

        return bodyDefinition.Id;
    }

    /// <summary>
    /// Registers one child-operation declaration beneath the current task. The declaration stores parent-side overrides
    /// for the imported child root while the child operation itself still owns the subtree that will be attached later.
    /// </summary>
    internal ChildDeclarationEntry AttachChildOperation(PlanItemDefinition parentDefinition, Type operationType, Func<OperationParameters> createParameters, string title, string? description)
    {
        _ = parentDefinition ?? throw new ArgumentNullException(nameof(parentDefinition));
        ChildDeclarationEntry declaration = AppendChildStep(parentDefinition.Id, ChildDeclarationKind.ChildOperation);
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

    internal void SetOperationParameters(PlanItemDefinition definition, OperationParameters operationParameters)
    {
        definition.OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
        definition.DeclaredOptionTypes = _declaredOptionTypes.ToList();
    }

    internal IReadOnlyList<ExecutionTaskId> GetCompletionTaskIds(ExecutionTaskId taskId)
    {
        PlanItemDefinition definition = GetDefinition(taskId);
        if (definition.ChildDeclarations.Count == 0)
        {
            return new[] { taskId };
        }

        ChildDeclarationEntry? lastDeclaration = definition.ChildDeclarations
            .OrderBy(entry => entry.Order)
            .LastOrDefault(entry => entry.CompletionTaskIds.Count > 0);
        return lastDeclaration?.CompletionTaskIds.Count > 0
            ? lastDeclaration.CompletionTaskIds.ToList()
            : new[] { taskId };
    }

    internal ChildDeclarationEntry AppendChildStep(ExecutionTaskId parentId, ChildDeclarationKind kind)
    {
        PlanItemDefinition definition = GetDefinition(parentId);
        ChildDeclarationEntry entry = new(NextOrderIndex(), kind);
        definition.ChildDeclarations.Add(entry);
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

    private PlanItemDefinition CreateItem(ExecutionTaskId id, string title, string? description, ExecutionTaskId? parentId)
    {
        return new PlanItemDefinition
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("Execution item title is required.", nameof(title)) : title,
            Description = description ?? string.Empty,
            ParentId = parentId,
            IsOperationRoot = parentId == null,
            Enabled = true,
            DisabledReason = string.Empty,
            Operation = _operation
        };
    }

    private void ExpandChildOperations()
    {
        List<(PlanItemDefinition Parent, ChildDeclarationEntry Declaration)> declarationsToExpand = _items
            .SelectMany(item => item.ChildDeclarations.Select(declaration => (Parent: item, Declaration: declaration)))
            .Where(tuple => tuple.Declaration.ChildOperationType != null)
            .ToList();

        foreach ((PlanItemDefinition definition, ChildDeclarationEntry declaration) in declarationsToExpand)
        {
            Operation childOperation = Operation.CreateOperation(declaration.ChildOperationType!);
            OperationParameters childParameters = declaration.CreateChildParameters!();
            ExecutionPlan childPlan = _buildChildPlan(childOperation, childParameters)
                ?? throw new InvalidOperationException($"Child operation '{declaration.ChildOperationType!.Name}' returned no execution plan during expansion.");
            ChildOperationRootOverrides rootOverrides = new(
                declaration.RootOverrides.Title,
                declaration.RootOverrides.Description,
                declaration.RootOverrides.IsHiddenInGraph);
            InsertedExecutionTasks insertedTasks = ExecutionTaskInsertion.InsertUnderParent(childPlan, definition.Id, rootOverrides);

            declaration.ChildOperationType = null;
            declaration.CreateChildParameters = null;
            declaration.RootTaskId = insertedTasks.RootTaskId;
            declaration.CompletionTaskIds.Clear();
            declaration.CompletionTaskIds.AddRange(GetLeafCompletionTaskIds(insertedTasks.Tasks));

            foreach (ExecutionTask childTask in insertedTasks.Tasks)
            {
                PlanItemDefinition importedDefinition = CreateItem(childTask.Id, childTask.Title, childTask.Description, childTask.ParentId);
                importedDefinition.Enabled = childTask.Enabled;
                importedDefinition.DisabledReason = childTask.DisabledReason;
                importedDefinition.OperationParameters = childTask.OperationParameters;
                importedDefinition.DeclaredOptionTypes = childTask.DeclaredOptionTypes.ToList();
                importedDefinition.ExecuteAsync = childTask.ExecuteAsync;
                importedDefinition.Operation = childTask.Operation;
                importedDefinition.IsOperationRoot = childTask.IsOperationRoot;
                importedDefinition.IsHiddenInGraph = childTask.IsHiddenInGraph;
                importedDefinition.DependencyIds.AddRange(childTask.DependsOn);
                AddImportedDefinition(importedDefinition);
            }
        }
    }

    /// <summary>
    /// Adds one imported task definition to the current plan and fails loudly if a supposedly unique task id collides.
    /// </summary>
    private void AddImportedDefinition(PlanItemDefinition definition)
    {
        _ = definition ?? throw new ArgumentNullException(nameof(definition));
        if (_items.Any(item => item.Id == definition.Id))
        {
            throw new InvalidOperationException($"Execution plan already contains task '{definition.Id}'. Imported child-operation task ids must be globally unique.");
        }

        _items.Add(definition);
    }

    private void ApplyChildDeclarationOrdering()
    {
        foreach (PlanItemDefinition definition in _items)
        {
            List<ChildDeclarationEntry> orderedDeclarations = definition.ChildDeclarations
                .OrderBy(entry => entry.Order)
                .ToList();
            List<ExecutionTaskId> previousCompletionTaskIds = new();

            foreach (ChildDeclarationEntry declaration in orderedDeclarations)
            {
                foreach (ExecutionTaskId entryTaskId in declaration.GetEntryTaskIds())
                {
                    PlanItemDefinition entryDefinition = GetDefinition(entryTaskId);
                    foreach (ExecutionTaskId previousCompletionTaskId in previousCompletionTaskIds)
                    {
                        AddDirectDependency(entryDefinition, previousCompletionTaskId);
                    }
                }

                if (declaration.CompletionTaskIds.Count > 0)
                {
                    previousCompletionTaskIds = declaration.CompletionTaskIds.ToList();
                }
            }
        }
    }

    private void AddDirectDependency(PlanItemDefinition definition, ExecutionTaskId dependencyId)
    {
        if (!definition.DependencyIds.Contains(dependencyId))
        {
            definition.DependencyIds.Add(dependencyId);
        }
    }

    private PlanItemDefinition GetDefinition(ExecutionTaskId taskId)
    {
        return _items.Single(item => item.Id == taskId);
    }

    private static string CreateBodyTaskTitle(PlanItemDefinition parentDefinition)
    {
        int bodyIndex = parentDefinition.ChildDeclarations.Count(entry => entry.Kind == ChildDeclarationKind.Body) + 1;
        return bodyIndex == 1
            ? parentDefinition.Title + ".Body"
            : parentDefinition.Title + ".Body " + bodyIndex;
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
    /// Generates a globally unique task identifier so embedded child plans can keep their authored ids without any
    /// parent-side remapping during plan composition or live runtime insertion.
    /// </summary>
    private ExecutionTaskId GenerateTaskId()
    {
        return ExecutionTaskId.New();
    }

    private int NextOrderIndex()
    {
        _nextSequence += 1;
        return _nextSequence;
    }

    internal sealed class PlanItemDefinition
    {
        public ExecutionTaskId Id { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public ExecutionTaskId? ParentId { get; set; }

        public List<ExecutionTaskId> DependencyIds { get; } = new();

        public bool Enabled { get; set; }

        public string DisabledReason { get; set; } = string.Empty;

        public Operation Operation { get; set; } = null!;

        public OperationParameters OperationParameters { get; set; } = null!;

        public List<Type> DeclaredOptionTypes { get; set; } = new();

        public Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync { get; set; }

        public List<ChildDeclarationEntry> ChildDeclarations { get; } = new();

        public bool IsOperationRoot { get; set; }

        public bool IsHiddenInGraph { get; set; }
    }

    internal sealed class ChildDeclarationEntry
    {
        public ChildDeclarationEntry(int order, ChildDeclarationKind kind)
        {
            Order = order;
            Kind = kind;
        }

        public int Order { get; }

        public ChildDeclarationKind Kind { get; }

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

internal enum ChildDeclarationKind
{
    Body,
    ChildTask,
    ParallelChildren,
    ChildOperation
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
