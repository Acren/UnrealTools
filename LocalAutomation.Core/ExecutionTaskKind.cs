namespace LocalAutomation.Core;

/// <summary>
/// Describes the role a task plays inside an execution plan so hosts can distinguish grouping containers from runnable
/// work without inventing their own heuristics.
/// </summary>
public enum ExecutionTaskKind
{
    /// <summary>
    /// Represents a visual grouping task that primarily organizes child work.
    /// </summary>
    Group,

    /// <summary>
    /// Represents a runnable operation or task.
    /// </summary>
    Task,

    /// <summary>
    /// Represents a smaller step within a broader task flow.
    /// </summary>
    Step
}
