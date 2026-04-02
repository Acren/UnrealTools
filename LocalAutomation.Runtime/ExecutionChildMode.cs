namespace LocalAutomation.Runtime;

/// <summary>
/// Controls whether repeated Task(...) calls inside one child scope should auto-sequence or remain independent siblings.
/// </summary>
public enum ExecutionChildMode
{
    /// <summary>
    /// Repeated Task(...) calls inside the scope auto-depend on the previous sibling.
    /// </summary>
    Sequenced,

    /// <summary>
    /// Repeated Task(...) calls inside the scope remain independent siblings unless the author adds explicit
    /// dependencies.
    /// </summary>
    Parallel,
}
