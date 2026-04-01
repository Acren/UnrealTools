using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Describes one authored task definition inside an execution plan, including its identity, structure, enablement, and
/// optional runtime callback.
/// </summary>
public sealed class ExecutionPlanTask
{
    /// <summary>
    /// Creates an authored execution-plan task with the provided metadata.
    /// </summary>
    public ExecutionPlanTask(
        ExecutionTaskId id,
        string title,
        string? description = null,
        ExecutionTaskId? parentId = null,
        IEnumerable<ExecutionTaskId>? dependsOn = null,
        bool enabled = true,
        string? disabledReason = null,
        OperationParameters? operationParameters = null,
        Func<ExecutionTaskContext, Task<OperationResult>>? executeAsync = null)
    {
        Id = id;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution task title is required.", nameof(title))
            : title;
        Description = description ?? string.Empty;
        ParentId = parentId;
        DependsOn = new ReadOnlyCollection<ExecutionTaskId>((dependsOn ?? Array.Empty<ExecutionTaskId>()).Distinct().ToList());
        Enabled = enabled;
        DisabledReason = enabled ? string.Empty : (disabledReason ?? string.Empty);
        OperationParameters = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
        ExecuteAsync = executeAsync;
    }

    /// <summary>
    /// Gets the stable task identifier used by preview and session views.
    /// </summary>
    public ExecutionTaskId Id { get; }

    /// <summary>
    /// Gets the short title rendered on the graph canvas.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the longer descriptive text shown in details panels when one exists.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the parent task identifier when this task participates in a visual hierarchy.
    /// </summary>
    public ExecutionTaskId? ParentId { get; }

    /// <summary>
    /// Gets the task identifiers that must complete before this task may run.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> DependsOn { get; }

    /// <summary>
    /// Gets whether the task is configured to participate in execution.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Gets the explanation for why this authored task is disabled.
    /// </summary>
    public string DisabledReason { get; }

    /// <summary>
    /// Gets the parameter state that authored this task so execution callbacks receive the same explicit runtime input
    /// even after operation instances stop storing execution-scoped mutable state.
    /// </summary>
    public OperationParameters OperationParameters { get; }

    /// <summary>
    /// Gets the optional runtime callback that executes this task when the scheduler reaches it.
    /// </summary>
    public Func<ExecutionTaskContext, Task<OperationResult>>? ExecuteAsync { get; }
}
