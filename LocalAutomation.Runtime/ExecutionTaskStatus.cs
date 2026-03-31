namespace LocalAutomation.Runtime;

/// <summary>
/// Represents the lifecycle state of an execution task.
/// </summary>
public enum ExecutionTaskStatus
{
    Planned,
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
    Disabled,
    Cancelled
}
