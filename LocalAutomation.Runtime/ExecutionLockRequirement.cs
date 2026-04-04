namespace LocalAutomation.Runtime;

/// <summary>
/// Describes one typed exclusive lock requirement declared by an operation so the runtime can coordinate shared mutable
/// resources without forcing operations to traffic in raw lock-key strings.
/// </summary>
public abstract class ExecutionLockRequirement
{
    /// <summary>
    /// Gets the normalized internal key used by the runtime lock table. Derived types own this translation so call sites
    /// stay strongly typed while the runtime still coordinates semaphores by one stable string key.
    /// </summary>
    public abstract string Key { get; }
}
