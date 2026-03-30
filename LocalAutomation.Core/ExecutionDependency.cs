namespace LocalAutomation.Core;

/// <summary>
/// Represents one dependency inside an execution plan DAG.
/// </summary>
public sealed class ExecutionDependency
{
    /// <summary>
    /// Creates a dependency from one task to another.
    /// </summary>
    public ExecutionDependency(ExecutionTaskId sourceTaskId, ExecutionTaskId targetTaskId)
    {
        SourceTaskId = sourceTaskId;
        TargetTaskId = targetTaskId;
    }

    /// <summary>
    /// Gets the task that must complete before the target task can start.
    /// </summary>
    public ExecutionTaskId SourceTaskId { get; }

    /// <summary>
    /// Gets the task that depends on the source task.
    /// </summary>
    public ExecutionTaskId TargetTaskId { get; }
}
