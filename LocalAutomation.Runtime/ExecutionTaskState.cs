namespace LocalAutomation.Runtime;

/// <summary>
/// Represents the rolled-up runtime execution state of an execution task subtree.
/// </summary>
public enum ExecutionTaskState
{
    /// <summary>
    /// The task subtree has been authored but no startable work in the subtree has begun yet.
    /// </summary>
    Planned,

    /// <summary>
    /// The task subtree has not started running yet, but some work is queued or waiting on dependencies or parent
    /// readiness.
    /// </summary>
    Pending,

    /// <summary>
    /// The task has been admitted into active execution and is now blocked only on acquiring its declared execution
    /// lock set. Parent scopes treat this as in-progress work rather than as untouched queued work.
    /// </summary>
    WaitingForExecutionLock,

    /// <summary>
    /// The task itself is running or the subtree still has active work that is not in the pure execution-lock wait
    /// shape. Parent tasks use this state for mixed in-progress subtrees, including runnable work and non-lock blockers.
    /// </summary>
    Running,

    /// <summary>
    /// The entire task subtree has reached a terminal state.
    /// </summary>
    Completed
}
