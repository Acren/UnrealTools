using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Represents one runnable work item inside a composite execution plan, including its scheduler dependencies and the
/// callback that performs the task's work.
/// </summary>
public sealed class ExecutionWorkItem
{
    /// <summary>
     /// Creates a scheduled work item for the provided execution-plan task.
     /// </summary>
    public ExecutionWorkItem(
        ExecutionTaskId taskId,
        string title,
        Func<ExecutionTaskContext, Task<OperationResult>> executeAsync,
        IEnumerable<ExecutionTaskId>? dependsOn = null,
        bool enabled = true,
        string? skippedReason = null)
    {
        TaskId = taskId;
        Title = string.IsNullOrWhiteSpace(title)
            ? throw new ArgumentException("Execution work item title is required.", nameof(title))
            : title;
        ExecuteAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        DependsOn = new ReadOnlyCollection<ExecutionTaskId>((dependsOn ?? Array.Empty<ExecutionTaskId>()).Distinct().ToList());
        Enabled = enabled;
        SkippedReason = skippedReason ?? string.Empty;
    }

    /// <summary>
    /// Gets the execution-plan step identifier owned by this work item.
    /// </summary>
    public ExecutionTaskId TaskId { get; }

    /// <summary>
    /// Gets the display title for the step.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the step identifiers that must complete successfully before this item may run.
    /// </summary>
    public IReadOnlyList<ExecutionTaskId> DependsOn { get; }

    /// <summary>
    /// Gets whether the step is enabled for the current build of the plan.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Gets the explanation for why the step is disabled or skipped.
    /// </summary>
    public string SkippedReason { get; }

    /// <summary>
    /// Gets the callback that performs the step's work.
    /// </summary>
    public Func<ExecutionTaskContext, Task<OperationResult>> ExecuteAsync { get; }
}
