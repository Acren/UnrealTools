namespace LocalAutomation.Core;

/// <summary>
/// Represents the lifecycle state of a planned task so preview and live sessions can share one status vocabulary.
/// </summary>
public enum ExecutionTaskStatus
{
    /// <summary>
    /// The task exists in the plan but has not been scheduled yet.
    /// </summary>
    Planned,

    /// <summary>
    /// The task is expected to run but has not started yet.
    /// </summary>
    Pending,

    /// <summary>
    /// The task cannot run because an upstream dependency failed and the scheduler will never start it.
    /// </summary>
    Blocked,

    /// <summary>
    /// The task is currently executing.
    /// </summary>
    Running,

    /// <summary>
    /// The task completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The task completed unsuccessfully.
    /// </summary>
    Failed,

    /// <summary>
    /// The task was intentionally skipped by configuration or execution policy.
    /// </summary>
    Skipped,

    /// <summary>
    /// The task could not run because configuration disabled it before execution started.
    /// </summary>
    Disabled,

    /// <summary>
    /// The task was cancelled before it finished.
    /// </summary>
    Cancelled
}
