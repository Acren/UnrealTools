using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds a previewable execution plan and, when callbacks are attached, the scheduled work items that execute it.
/// Operations use typed task handles from this builder instead of handwritten string identifiers.
/// </summary>
public sealed class ExecutionPlanBuilder
{
    private readonly ExecutionPlanId _planId;
    private readonly string _title;
    private readonly List<PlanItemDefinition> _items = new();
    private readonly Func<Operation, OperationParameters, ExecutionPlan?> _buildChildPlan;
    // The builder increments this monotonically so auto-generated ids stay deterministic within one plan build.
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
    /// Declares one task in the plan and returns its fluent builder. Exactly one root task is allowed; additional tasks
    /// must be declared beneath that root.
    /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null, ExecutionTaskHandle parent = default)
    {
        ValidateParent(parent);
        PlanItemDefinition definition = CreateItem(GenerateTaskId(), title, description, parent.IsValid ? parent.Id : null);
        _items.Add(definition);
        return new ExecutionTaskBuilder(this, definition, parent);
    }

    /// <summary>
    /// Opening a root-level sibling scope is no longer supported because execution plans now require exactly one real
    /// root task. Declare the root with Task(...), then call root.Children(...).
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
                executeAsync: item.ExecuteAsync))
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

    internal void AttachCallback(PlanItemDefinition definition, Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        definition.ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
    }

    internal void AttachChildOperation(PlanItemDefinition definition, Type operationType, Func<OperationParameters> createParameters)
    {
        if (definition.ExecuteAsync != null)
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
                importedDefinition.DependencyIds.AddRange(childTask.DependsOn);
                _items.Add(importedDefinition);
            }
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

        public Type? ChildOperationType { get; set; }

        public Func<OperationParameters>? CreateChildParameters { get; set; }
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

        /* Generate every inserted id up front so parent and dependency references can be rewritten while
           preserving the authored task order from the child plan. */
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
            insertedTasks.Add(new ExecutionTask(
                remappedTaskId,
                childTask.Title,
                childTask.Description,
                remappedParentId,
                childTask.DependsOn.Select(dependencyId => remappedIds[dependencyId]).ToList(),
                childTask.Enabled,
                childTask.DisabledReason,
                childTask.OperationParameters,
                childTask.ExecuteAsync));
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
