using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds a previewable execution plan and, when callbacks are attached, the scheduled work items that execute it.
/// Operations use typed handles from this builder instead of handwritten string identifiers.
/// </summary>
public sealed class ExecutionPlanBuilder
{
    private readonly ExecutionPlanId _planId;
    private readonly string _title;
    private readonly List<PlanItemDefinition> _items = new();
    // The builder increments this monotonically so auto-generated ids stay deterministic within one plan build.
    private int _nextSequence = 0;

    /// <summary>
    /// Creates a builder for one composite execution plan.
    /// </summary>
    public ExecutionPlanBuilder(string title, ExecutionPlanId? planId = null)
    {
        _title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution plan title is required.", nameof(title))
            : title;
        _planId = planId ?? ExecutionIdentifierFactory.CreatePlanId(title);
    }

    /// <summary>
    /// Declares a visual grouping in the plan and returns a typed handle that later steps can use as their parent.
    /// </summary>
    public ExecutionGroupHandle Group(string title, string? description = null, ExecutionGroupHandle parent = default)
    {
        PlanItemDefinition definition = CreateItem(GenerateTaskId(ExecutionTaskKind.Group, title), title, description, ExecutionTaskKind.Group, parent.IsValid ? parent.Id : null);
        _items.Add(definition);
        return new ExecutionGroupHandle(definition.Id);
    }

    /// <summary>
    /// Declares a visual grouping with an explicit stable identifier so independently authored plans can share the same
    /// runtime task identities.
    /// </summary>
    public ExecutionGroupHandle Group(ExecutionTaskId id, string title, string? description = null, ExecutionGroupHandle parent = default)
    {
        PlanItemDefinition definition = CreateItem(id, title, description, ExecutionTaskKind.Group, parent.IsValid ? parent.Id : null);
        _items.Add(definition);
        return new ExecutionGroupHandle(definition.Id);
    }

    /// <summary>
    /// Starts a new step declaration and returns a fluent builder that can attach dependencies and execution code.
    /// </summary>
    public ExecutionStepBuilder Step(string title, string? description = null, ExecutionGroupHandle parent = default)
    {
        PlanItemDefinition definition = CreateItem(GenerateTaskId(ExecutionTaskKind.Step, title), title, description, ExecutionTaskKind.Step, parent.IsValid ? parent.Id : null);
        _items.Add(definition);
        return new ExecutionStepBuilder(this, definition);
    }

    /// <summary>
    /// Starts a new step declaration with an explicit stable identifier so preview and runtime authoring can target the
    /// same task identity without hand-built string ids.
    /// </summary>
    public ExecutionStepBuilder Step(ExecutionTaskId id, string title, string? description = null, ExecutionGroupHandle parent = default)
    {
        PlanItemDefinition definition = CreateItem(id, title, description, ExecutionTaskKind.Step, parent.IsValid ? parent.Id : null);
        _items.Add(definition);
        return new ExecutionStepBuilder(this, definition);
    }

    /// <summary>
    /// Starts a fluent linear sequence within the provided group so consecutive steps chain automatically.
    /// </summary>
    public ExecutionSequenceBuilder Sequence(ExecutionGroupHandle parent = default)
    {
        return new ExecutionSequenceBuilder(this, parent);
    }

    /// <summary>
    /// Builds the preview plan used by the UI.
    /// </summary>
    public ExecutionPlan BuildPlan()
    {
        List<ExecutionTask> tasks = _items
            .Select(item => new ExecutionTask(
                id: item.Id,
                title: item.Title,
                description: item.Description,
                kind: item.Kind,
                parentId: item.ParentId,
                status: item.Enabled ? ExecutionTaskStatus.Ready : ExecutionTaskStatus.Disabled,
                statusReason: item.Enabled ? string.Empty : item.DisabledReason))
            .ToList();
        List<ExecutionDependency> dependencies = _items
            .SelectMany(item => item.DependencyIds.Select(dependencyId => new ExecutionDependency(dependencyId, item.Id)))
            .ToList();
        return new ExecutionPlan(_planId, _title, tasks, dependencies);
    }

    /// <summary>
    /// Builds the scheduler work items for the declared executable steps.
    /// </summary>
    public IReadOnlyList<ExecutionWorkItem> BuildWorkItems()
    {
        return _items
            .Where(item => item.Kind != ExecutionTaskKind.Group && item.ExecuteAsync != null)
            .Select(item =>
            {
                return new ExecutionWorkItem(
                    taskId: item.Id,
                    title: item.Title,
                    executeAsync: item.ExecuteAsync!,
                    dependsOn: item.DependencyIds,
                    enabled: item.Enabled,
                    skippedReason: item.DisabledReason);
            })
            .ToList();
    }

    internal void AddDependency(PlanItemDefinition definition, ExecutionStepHandle dependency)
    {
        if (!dependency.IsValid)
        {
            throw new ArgumentException("Execution step dependency handle is not valid.", nameof(dependency));
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

    internal ExecutionTaskId GetStepId(ExecutionStepHandle handle)
    {
        if (!handle.IsValid)
        {
            throw new ArgumentException("Execution step handle is not valid.", nameof(handle));
        }

        return handle.Id;
    }

    internal ExecutionStepHandle AttachCallback(PlanItemDefinition definition, Func<ExecutionTaskContext, Task<OperationResult>> executeAsync)
    {
        definition.ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        return new ExecutionStepHandle(definition.Id);
    }

    internal ExecutionStepHandle FinalizeStep(PlanItemDefinition definition)
    {
        return new ExecutionStepHandle(definition.Id);
    }

    private PlanItemDefinition CreateItem(ExecutionTaskId id, string title, string? description, ExecutionTaskKind kind, ExecutionTaskId? parentId)
    {
        return new PlanItemDefinition
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("Execution item title is required.", nameof(title)) : title,
            Description = description ?? string.Empty,
            Kind = kind,
            ParentId = parentId,
            Enabled = true,
            DisabledReason = string.Empty
        };
    }

    /// <summary>
    /// Generates a deterministic task identifier for builder-authored nodes when callers do not provide an explicit one.
    /// </summary>
    private ExecutionTaskId GenerateTaskId(ExecutionTaskKind kind, string title)
    {
        return ExecutionIdentifierFactory.CreateTaskId(_planId, kind.ToString(), _nextSequence.ToString("D3"), title);
    }
    internal sealed class PlanItemDefinition
    {
        public ExecutionTaskId Id { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public ExecutionTaskKind Kind { get; set; }

        public ExecutionTaskId? ParentId { get; set; }

        public List<ExecutionTaskId> DependencyIds { get; } = new();

        public bool Enabled { get; set; }

        public string DisabledReason { get; set; } = string.Empty;

        public Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync { get; set; }
    }
}
