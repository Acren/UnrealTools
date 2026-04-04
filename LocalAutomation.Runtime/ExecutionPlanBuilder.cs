using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds a previewable execution plan where task callbacks, child scopes, and expanded child operations all participate
/// in one ordered child-declaration stream beneath each parent task.
/// </summary>
public sealed class ExecutionPlanBuilder
{
    private readonly ExecutionPlanId _planId;
    private readonly string _title;
    private readonly List<PlanItemDefinition> _items = new();
    private readonly Func<Operation, OperationParameters, ExecutionPlan?> _buildChildPlan;
    private IReadOnlyCollection<Type> _declaredOptionTypes = Array.Empty<Type>();
    private int _nextSequence = 0;

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
    public ExecutionTaskBuilder Task(string title, string? description = null, ExecutionTaskHandle parent = default)
    {
        ValidateParent(parent);
        PlanItemDefinition definition = CreateItem(GenerateTaskId(), title, description, parent.IsValid ? parent.Id : null);
        _items.Add(definition);
        return new ExecutionTaskBuilder(this, definition, parent);
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
                isCallbackTask: item.IsCallbackTask,
                callbackOwnerTaskId: item.CallbackOwnerTaskId))
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

    internal void SetDeclaredOptionTypes(IEnumerable<Type> declaredOptionTypes)
    {
        _declaredOptionTypes = (declaredOptionTypes ?? throw new ArgumentNullException(nameof(declaredOptionTypes))).ToList();
    }

    internal void AddDependency(PlanItemDefinition definition, ExecutionTaskHandle dependency)
    {
        if (!dependency.IsValid)
        {
            throw new ArgumentException("Execution task dependency handle is not valid.", nameof(dependency));
        }

        foreach (ExecutionTaskId completionTaskId in GetCompletionTaskIds(dependency.Id))
        {
            AddDependency(definition, completionTaskId);
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

    internal ExecutionTaskId GetTaskId(ExecutionTaskHandle handle)
    {
        if (!handle.IsValid)
        {
            throw new ArgumentException("Execution task handle is not valid.", nameof(handle));
        }

        return handle.Id;
    }

    internal ExecutionTaskHandle AttachCallback(PlanItemDefinition parentDefinition, Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        _ = parentDefinition ?? throw new ArgumentNullException(nameof(parentDefinition));
        Func<ExecutionTaskContext, Task<OperationResult>> resolvedExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));

        PlanItemDefinition callbackDefinition = CreateItem(
            GenerateTaskId(),
            CreateCallbackTitle(parentDefinition),
            parentDefinition.Description,
            parentDefinition.Id);
        callbackDefinition.Enabled = parentDefinition.Enabled;
        callbackDefinition.DisabledReason = parentDefinition.DisabledReason;
        callbackDefinition.OperationParameters = parentDefinition.OperationParameters;
        callbackDefinition.DeclaredOptionTypes = parentDefinition.DeclaredOptionTypes.ToList();
        callbackDefinition.ExecuteAsync = resolvedExecuteAsync;
        callbackDefinition.IsOperationRoot = false;
        callbackDefinition.IsCallbackTask = true;
        callbackDefinition.CallbackOwnerTaskId = parentDefinition.Id;
        _items.Add(callbackDefinition);

        ChildDeclarationEntry declaration = new(NextOrderIndex(), ChildDeclarationKind.Callback);
        declaration.EntryTaskIds.Add(callbackDefinition.Id);
        declaration.CompletionTaskIds.Add(callbackDefinition.Id);
        parentDefinition.ChildDeclarations.Add(declaration);

        return FinalizeTask(callbackDefinition);
    }

    internal void AttachChildOperation(PlanItemDefinition parentDefinition, Type operationType, Func<OperationParameters> createParameters)
    {
        _ = parentDefinition ?? throw new ArgumentNullException(nameof(parentDefinition));
        ChildDeclarationEntry declaration = new(NextOrderIndex(), ChildDeclarationKind.ChildOperation)
        {
            ChildOperationType = operationType ?? throw new ArgumentNullException(nameof(operationType)),
            CreateChildParameters = createParameters ?? throw new ArgumentNullException(nameof(createParameters))
        };
        parentDefinition.ChildDeclarations.Add(declaration);
    }

    internal void SetOperationParameters(PlanItemDefinition definition, OperationParameters operationParameters)
    {
        definition.OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
        definition.DeclaredOptionTypes = _declaredOptionTypes.ToList();
    }

    internal ExecutionTaskHandle FinalizeTask(PlanItemDefinition definition)
    {
        return new ExecutionTaskHandle(definition.Id);
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

    internal ChildDeclarationEntry RegisterChildScopeEntry(ExecutionTaskHandle parent)
    {
        ExecutionTaskId parentId = GetTaskId(parent);
        PlanItemDefinition definition = GetDefinition(parentId);
        ChildDeclarationEntry entry = new(NextOrderIndex(), ChildDeclarationKind.ChildScope);
        definition.ChildDeclarations.Add(entry);
        return entry;
    }

    internal ChildDeclarationEntry RegisterChildScopeTaskEntry(ExecutionTaskHandle parent, PlanItemDefinition taskDefinition)
    {
        _ = taskDefinition ?? throw new ArgumentNullException(nameof(taskDefinition));
        ChildDeclarationEntry entry = RegisterChildScopeEntry(parent);
        entry.EntryTaskIds.Add(taskDefinition.Id);
        entry.CompletionTaskIds.Add(taskDefinition.Id);
        return entry;
    }

    internal ChildDeclarationEntry RegisterSequentialSiblingEntry(ExecutionTaskHandle parent, PlanItemDefinition taskDefinition)
    {
        _ = taskDefinition ?? throw new ArgumentNullException(nameof(taskDefinition));
        ChildDeclarationEntry entry = RegisterChildScopeEntry(parent);
        entry.EntryTaskIds.Add(taskDefinition.Id);
        entry.CompletionTaskIds.Add(taskDefinition.Id);
        return entry;
    }

    internal void AddScopeEntryTask(ChildDeclarationEntry declaration, PlanItemDefinition taskDefinition)
    {
        _ = declaration ?? throw new ArgumentNullException(nameof(declaration));
        _ = taskDefinition ?? throw new ArgumentNullException(nameof(taskDefinition));

        if (!declaration.EntryTaskIds.Contains(taskDefinition.Id))
        {
            declaration.EntryTaskIds.Add(taskDefinition.Id);
        }
    }

    internal void SetScopeCompletionTask(ChildDeclarationEntry declaration, PlanItemDefinition taskDefinition)
    {
        _ = declaration ?? throw new ArgumentNullException(nameof(declaration));
        _ = taskDefinition ?? throw new ArgumentNullException(nameof(taskDefinition));

        declaration.CompletionTaskIds.Clear();
        declaration.CompletionTaskIds.Add(taskDefinition.Id);
    }

    internal void AddScopeCompletionTask(ChildDeclarationEntry declaration, PlanItemDefinition taskDefinition)
    {
        _ = declaration ?? throw new ArgumentNullException(nameof(declaration));
        _ = taskDefinition ?? throw new ArgumentNullException(nameof(taskDefinition));

        if (!declaration.CompletionTaskIds.Contains(taskDefinition.Id))
        {
            declaration.CompletionTaskIds.Add(taskDefinition.Id);
        }
    }

    private void ValidateParent(ExecutionTaskHandle parent)
    {
        if (parent.IsValid)
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
            DisabledReason = string.Empty
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
            InsertedExecutionTasks insertedTasks = ExecutionTaskInsertion.InsertUnderParent(childPlan, definition.Id, GenerateTaskId);

            declaration.ChildOperationType = null;
            declaration.CreateChildParameters = null;
            declaration.EntryTaskIds.Clear();
            declaration.EntryTaskIds.Add(insertedTasks.RootTaskId);
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
                importedDefinition.IsOperationRoot = childTask.IsOperationRoot;
                importedDefinition.IsCallbackTask = childTask.IsCallbackTask;
                importedDefinition.CallbackOwnerTaskId = childTask.CallbackOwnerTaskId;
                importedDefinition.DependencyIds.AddRange(childTask.DependsOn);
                _items.Add(importedDefinition);
            }
        }
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
                if (declaration.EntryTaskIds.Count > 0)
                {
                    foreach (ExecutionTaskId entryTaskId in declaration.EntryTaskIds)
                    {
                        PlanItemDefinition entryDefinition = GetDefinition(entryTaskId);
                        foreach (ExecutionTaskId previousCompletionTaskId in previousCompletionTaskIds)
                        {
                            AddDependency(entryDefinition, previousCompletionTaskId);
                        }
                    }
                }

                if (declaration.CompletionTaskIds.Count > 0)
                {
                    previousCompletionTaskIds = declaration.CompletionTaskIds.ToList();
                }
            }
        }
    }

    private void AddDependency(PlanItemDefinition definition, ExecutionTaskId dependencyId)
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

    private static string CreateCallbackTitle(PlanItemDefinition parentDefinition)
    {
        int callbackIndex = parentDefinition.ChildDeclarations.Count(entry => entry.Kind == ChildDeclarationKind.Callback) + 1;
        return callbackIndex == 1
            ? parentDefinition.Title + ".Callback"
            : parentDefinition.Title + ".Callback " + callbackIndex;
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
    /// Generates a deterministic task identifier for builder-authored nodes when callers do not provide an explicit one.
    /// </summary>
    private ExecutionTaskId GenerateTaskId()
    {
        _nextSequence += 1;
        return ExecutionIdentifierFactory.CreateTaskId(_planId, "task", _nextSequence.ToString("D6"));
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

        public OperationParameters OperationParameters { get; set; } = null!;

        public List<Type> DeclaredOptionTypes { get; set; } = new();

        public Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync { get; set; }

        public List<ChildDeclarationEntry> ChildDeclarations { get; } = new();

        public bool IsOperationRoot { get; set; }

        public bool IsCallbackTask { get; set; }

        public ExecutionTaskId? CallbackOwnerTaskId { get; set; }
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

        public List<ExecutionTaskId> EntryTaskIds { get; } = new();

        public List<ExecutionTaskId> CompletionTaskIds { get; } = new();

        public Type? ChildOperationType { get; set; }

        public Func<OperationParameters>? CreateChildParameters { get; set; }
    }
}

internal enum ChildDeclarationKind
{
    Callback,
    ChildScope,
    ChildOperation
}

/// <summary>
/// Inserts one execution plan beneath an existing parent task by remapping every task id, preserving authored order, and
/// rewriting parent/dependency links to the newly inserted ids. Both build-time expansion and runtime child insertion use
/// this same graph-insertion path so child operations always materialize as normal nested tasks.
/// </summary>
internal static class ExecutionTaskInsertion
{
    public static InsertedExecutionTasks InsertUnderParent(ExecutionPlan childPlan, ExecutionTaskId parentTaskId, Func<ExecutionTaskId> generateTaskId)
    {
        if (childPlan == null)
        {
            throw new ArgumentNullException(nameof(childPlan));
        }

        if (generateTaskId == null)
        {
            throw new ArgumentNullException(nameof(generateTaskId));
        }

        IReadOnlyList<ExecutionTask> orderedTasks = childPlan.Tasks.ToList();
        ExecutionTask childRoot = orderedTasks.Single(task => task.ParentId == null);
        Dictionary<ExecutionTaskId, ExecutionTaskId> remappedIds = new();

        foreach (ExecutionTask childTask in orderedTasks)
        {
            remappedIds[childTask.Id] = generateTaskId();
        }

        List<ExecutionTask> insertedTasks = new(orderedTasks.Count);
        foreach (ExecutionTask childTask in orderedTasks)
        {
            ExecutionTaskId remappedTaskId = remappedIds[childTask.Id];
            ExecutionTaskId? remappedParentId = childTask.ParentId == null
                ? parentTaskId
                : remappedIds[childTask.ParentId.Value];
            ExecutionTaskId? remappedCallbackOwnerTaskId = childTask.CallbackOwnerTaskId == null
                ? null
                : remappedIds[childTask.CallbackOwnerTaskId.Value];
            insertedTasks.Add(new ExecutionTask(
                remappedTaskId,
                childTask.Title,
                childTask.Description,
                remappedParentId,
                childTask.DependsOn.Select(dependencyId => remappedIds[dependencyId]).ToList(),
                childTask.Enabled,
                childTask.DisabledReason,
                childTask.OperationParameters,
                childTask.DeclaredOptionTypes,
                childTask.ExecuteAsync,
                outcome: null,
                isOperationRoot: childTask.IsOperationRoot,
                isCallbackTask: childTask.IsCallbackTask,
                callbackOwnerTaskId: remappedCallbackOwnerTaskId));
        }

        return new InsertedExecutionTasks(insertedTasks, remappedIds[childRoot.Id]);
    }
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
