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
    /// Declares one root task in the plan and returns its fluent builder.
    /// </summary>
    public ExecutionTaskBuilder Task(string title, string? description = null, ExecutionTaskHandle parent = default)
    {
        PlanItemDefinition definition = CreateItem(GenerateTaskId(title), title, description, parent.IsValid ? parent.Id : null);
        _items.Add(definition);
        return new ExecutionTaskBuilder(this, definition, parent);
    }

    /// <summary>
    /// Declares one task with an explicit stable identifier so independently authored plans can share the same runtime
    /// task identities.
    /// </summary>
    public ExecutionTaskBuilder Task(ExecutionTaskId id, string title, string? description = null, ExecutionTaskHandle parent = default)
    {
        PlanItemDefinition definition = CreateItem(id, title, description, parent.IsValid ? parent.Id : null);
        _items.Add(definition);
        return new ExecutionTaskBuilder(this, definition, parent);
    }

    /// <summary>
    /// Opens the root task scope so repeated Task(...) calls create sequential siblings at the plan root.
    /// </summary>
    public void Children(Action<ExecutionTaskScopeBuilder> build)
    {
        if (build == null)
        {
            throw new ArgumentNullException(nameof(build));
        }

        build(new ExecutionTaskScopeBuilder(this, default));
    }

    /// <summary>
    /// Builds the preview plan used by the UI.
    /// </summary>
    public ExecutionPlan BuildPlan()
    {
        /* Preview plans should preserve the distinction between planned work and queued runtime work. Enabled tasks stay
           Planned in the preview graph, and runtime session updates transition them to Pending when execution starts. */
        List<ExecutionPlanTask> tasks = _items
            .Select(item => new ExecutionPlanTask(
                id: item.Id,
                title: item.Title,
                description: item.Description,
                parentId: item.ParentId,
                status: item.Enabled ? ExecutionTaskStatus.Planned : ExecutionTaskStatus.Disabled,
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
            .Where(item => item.ExecuteAsync != null)
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

    internal ExecutionTaskHandle FinalizeTask(PlanItemDefinition definition)
    {
        return new ExecutionTaskHandle(definition.Id);
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

    /// <summary>
    /// Generates a deterministic task identifier for builder-authored nodes when callers do not provide an explicit one.
    /// </summary>
    private ExecutionTaskId GenerateTaskId(string title)
    {
        return ExecutionIdentifierFactory.CreateTaskId(_planId, "task", _nextSequence.ToString("D3"), title);
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

        public Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync { get; set; }
    }
}
