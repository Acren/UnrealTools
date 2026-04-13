namespace LocalAutomation.Runtime;

/// <summary>
/// Represents the rolled-up runtime execution state of an execution task subtree.
/// Ordering matters: every started-but-non-terminal runtime state must remain numerically between <see cref="Queued"/>
/// and <see cref="Completed"/> so lifecycle code can classify in-progress work with one range comparison.
/// </summary>
public enum ExecutionTaskState
{
    /// <summary>
    /// The task subtree has been authored but no startable work in the subtree has begun yet.
    /// </summary>
    Planned,

    /// <summary>
    /// The task subtree has not started running yet, and its next reachable work is still untouched queued work.
    /// </summary>
    Queued,

    /// <summary>
    /// The task subtree has already started earlier, but no work inside it is currently active and the remaining
    /// reachable frontier is blocked on dependencies outside the subtree.
    /// </summary>
    AwaitingDependency = 2,

    /// <summary>
    /// Backward-compatible alias for <see cref="AwaitingDependency"/>.
    /// </summary>
    WaitingForDependencies = AwaitingDependency,

    /// <summary>
    /// The task has been admitted into active execution and is now blocked only on acquiring its declared execution
    /// lock set. Parent scopes treat this as in-progress work rather than as untouched queued work.
    /// </summary>
    AwaitingLock = 3,

    /// <summary>
    /// Backward-compatible alias for <see cref="AwaitingLock"/>.
    /// </summary>
    WaitingForExecutionLock = AwaitingLock,

    /// <summary>
    /// The task itself is running or the subtree still has active work that is not in the pure execution-lock wait
    /// shape. Parent tasks use this state for mixed in-progress subtrees, including runnable work and non-lock blockers.
    /// </summary>
    Running = 4,

    /// <summary>
    /// The entire task subtree has reached a terminal state.
    /// </summary>
    Completed = 5
}
