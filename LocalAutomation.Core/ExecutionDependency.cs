using System;

namespace LocalAutomation.Core;

/// <summary>
/// Represents one dependency inside an execution plan DAG.
/// </summary>
public sealed class ExecutionDependency
{
    /// <summary>
    /// Creates a dependency from one task to another.
    /// </summary>
    public ExecutionDependency(string sourceTaskId, string targetTaskId)
    {
        SourceTaskId = string.IsNullOrWhiteSpace(sourceTaskId)
            ? throw new ArgumentException("Execution dependency source task id is required.", nameof(sourceTaskId))
            : sourceTaskId;
        TargetTaskId = string.IsNullOrWhiteSpace(targetTaskId)
            ? throw new ArgumentException("Execution dependency target task id is required.", nameof(targetTaskId))
            : targetTaskId;
    }

    /// <summary>
    /// Gets the task that must complete before the target task can start.
    /// </summary>
    public string SourceTaskId { get; }

    /// <summary>
    /// Gets the task that depends on the source task.
    /// </summary>
    public string TargetTaskId { get; }
}
