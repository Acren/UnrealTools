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
    /// The task is eligible to run except for its declared execution lock set. No task body runs in this state; the
    /// scheduler will start the body only after the global lock coordinator grants every required lock.
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
