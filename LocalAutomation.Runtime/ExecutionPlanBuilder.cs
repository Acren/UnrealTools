using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds a previewable execution plan and lowers authored tasks into container nodes plus explicit child work items.
/// Authored declaration order is preserved, while serial ordering is expressed through dependencies instead of special
/// scheduler modes.
/// </summary>
public sealed class ExecutionPlanBuilder
{
    private readonly ExecutionPlanId _planId;
    private readonly string _title;
    private readonly List<PlanItemDefinition> _items = new();
    private readonly Func<Operation, OperationParameters, ExecutionPlan?> _buildChildPlan;
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
        LowerImplicitCallbackTasks();
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
                executeAsync: item.ExecuteAsync,
                result: null,
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

    internal void AddDependency(PlanItemDefinition definition, ExecutionTaskHandle dependency)
    {
        if (!dependency.IsValid)
        {
            throw new ArgumentException("Execution task dependency handle is not valid.", nameof(dependency));
        }

        if (!definition.DependencyIds.Contains(dependency.Id))
        {
            definition.DependencyIds.Add(dependency.Id);
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

    /// <summary>
    /// Records one authored callback declaration at its exact call-site order so lowering can materialize a visible
    /// `.Callback` child task without losing authored ordering relative to other child content.
    /// </summary>
    internal void AttachCallback(PlanItemDefinition definition, Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        if (definition.CallbackEntry != null)
        {
            throw new InvalidOperationException("Execution tasks cannot declare more than one direct callback.");
        }

        definition.CallbackEntry = new CallbackPlanEntry(executeAsync ?? throw new ArgumentNullException(nameof(executeAsync)), NextOrderIndex());
    }

    internal void AttachChildOperation(PlanItemDefinition definition, Type operationType, Func<OperationParameters> createParameters)
    {
        if (definition.CallbackEntry != null)
        {
            throw new InvalidOperationException("Execution tasks cannot define both a direct callback and a child operation expansion.");
        }

        definition.ChildOperationType = operationType ?? throw new ArgumentNullException(nameof(operationType));
        definition.CreateChildParameters = createParameters ?? throw new ArgumentNullException(nameof(createParameters));
    }

    internal void SetOperationParameters(PlanItemDefinition definition, OperationParameters operationParameters)
    {
        definition.OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
    }

    internal ExecutionTaskHandle FinalizeTask(PlanItemDefinition definition)
    {
        return new ExecutionTaskHandle(definition.Id);
    }

    /// <summary>
    /// Registers the declaration order of one child scope beneath the provided parent task so later callback lowering can
    /// preserve the authored order between callbacks and child groups.
    /// </summary>
    internal ChildScopePlanEntry RegisterChildScopeEntry(ExecutionTaskHandle parent)
    {
        ExecutionTaskId parentId = GetTaskId(parent);
        PlanItemDefinition definition = _items.Single(item => item.Id == parentId);
        ChildScopePlanEntry entry = new(NextOrderIndex());
        definition.ChildScopeEntries.Add(entry);
        return entry;
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
            Enabled = true,
            DisabledReason = string.Empty
        };
    }

    private void ExpandChildOperations()
    {
        List<PlanItemDefinition> definitionsToExpand = _items
            .Where(item => item.ChildOperationType != null)
            .ToList();

        foreach (PlanItemDefinition definition in definitionsToExpand)
        {
            Operation childOperation = Operation.CreateOperation(definition.ChildOperationType!);
            OperationParameters childParameters = definition.CreateChildParameters!();
            ExecutionPlan childPlan = _buildChildPlan(childOperation, childParameters)
                ?? throw new InvalidOperationException($"Child operation '{definition.ChildOperationType!.Name}' returned no execution plan during expansion.");
            InsertedExecutionTasks insertedTasks = ExecutionTaskInsertion.InsertUnderParent(childPlan, definition.Id, GenerateTaskId);

            definition.ChildOperationType = null;
            definition.CreateChildParameters = null;

            foreach (ExecutionTask childTask in insertedTasks.Tasks)
            {
                PlanItemDefinition importedDefinition = CreateItem(childTask.Id, childTask.Title, childTask.Description, childTask.ParentId);
                importedDefinition.Enabled = childTask.Enabled;
                importedDefinition.DisabledReason = childTask.DisabledReason;
                importedDefinition.OperationParameters = childTask.OperationParameters;
                importedDefinition.ExecuteAsync = childTask.ExecuteAsync;
                importedDefinition.IsCallbackTask = childTask.IsCallbackTask;
                importedDefinition.CallbackOwnerTaskId = childTask.CallbackOwnerTaskId;
                importedDefinition.DependencyIds.AddRange(childTask.DependsOn);
                _items.Add(importedDefinition);
            }
        }
    }

    /// <summary>
    /// Lowers direct authored callbacks into visible `.Callback` child tasks that participate in the same normal child
    /// graph as every other child task. Declaration order is preserved by inserting dependencies from earlier child-scope
    /// groups to later callback children when needed.
    /// </summary>
    private void LowerImplicitCallbackTasks()
    {
        List<PlanItemDefinition> definitionsWithCallbacks = _items
            .Where(item => item.CallbackEntry != null)
            .ToList();

        foreach (PlanItemDefinition definition in definitionsWithCallbacks)
        {
            CallbackPlanEntry callbackEntry = definition.CallbackEntry!;
            ExecutionTaskId callbackTaskId = GenerateTaskId();
            PlanItemDefinition callbackDefinition = CreateItem(callbackTaskId, definition.Title + ".Callback", definition.Description, definition.Id);
            callbackDefinition.Enabled = definition.Enabled;
            callbackDefinition.DisabledReason = definition.DisabledReason;
            callbackDefinition.OperationParameters = definition.OperationParameters;
            callbackDefinition.ExecuteAsync = callbackEntry.ExecuteAsync;
            callbackDefinition.IsCallbackTask = true;
            callbackDefinition.CallbackOwnerTaskId = definition.Id;
            callbackDefinition.DependencyIds.AddRange(definition.DependencyIds);

            /* Child scope declarations remain independent by default, so the callback only depends on the trailing task
               from child scopes that were authored earlier than the callback declaration point. */
            foreach (ChildScopePlanEntry childScopeEntry in definition.ChildScopeEntries.Where(entry => entry.Order < callbackEntry.Order))
            {
                if (childScopeEntry.LastTaskId != null && !callbackDefinition.DependencyIds.Contains(childScopeEntry.LastTaskId.Value))
                {
                    callbackDefinition.DependencyIds.Add(childScopeEntry.LastTaskId.Value);
                }
            }

            foreach (ChildScopePlanEntry childScopeEntry in definition.ChildScopeEntries.Where(entry => entry.Order > callbackEntry.Order))
            {
                if (childScopeEntry.FirstTaskId != null && !childScopeEntry.FirstDefinition!.DependencyIds.Contains(callbackTaskId))
                {
                    childScopeEntry.FirstDefinition!.DependencyIds.Add(callbackTaskId);
                }
            }

            _items.Add(callbackDefinition);
            definition.ExecuteAsync = null;
            definition.CallbackEntry = null;
            definition.DependencyIds.Clear();
        }
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

        public Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync { get; set; }

        public CallbackPlanEntry? CallbackEntry { get; set; }

        public List<ChildScopePlanEntry> ChildScopeEntries { get; } = new();

        public Type? ChildOperationType { get; set; }

        public Func<OperationParameters>? CreateChildParameters { get; set; }

        public bool IsCallbackTask { get; set; }

        public ExecutionTaskId? CallbackOwnerTaskId { get; set; }
    }

    internal sealed class CallbackPlanEntry
    {
        public CallbackPlanEntry(Func<ExecutionTaskContext, Task<OperationResult>> executeAsync, int order)
        {
            ExecuteAsync = executeAsync;
            Order = order;
        }

        public Func<ExecutionTaskContext, Task<OperationResult>> ExecuteAsync { get; }

        public int Order { get; }
    }

    internal sealed class ChildScopePlanEntry
    {
        public ChildScopePlanEntry(int order)
        {
            Order = order;
        }

        public int Order { get; }

        public ExecutionTaskId? FirstTaskId { get; set; }

        public PlanItemDefinition? FirstDefinition { get; set; }

        public ExecutionTaskId? LastTaskId { get; set; }
    }
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
                childTask.ExecuteAsync,
                result: null,
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
