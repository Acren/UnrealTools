namespace LocalAutomation.Runtime;

/// <summary>
/// Represents the runtime execution state of an execution task.
/// </summary>
public enum ExecutionTaskState
{
    Planned,
    Pending,
    Running,
    Completed
}
