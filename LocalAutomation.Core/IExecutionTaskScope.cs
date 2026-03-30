namespace LocalAutomation.Core;

/// <summary>
/// Exposes the current task scope carried by a logger so nested runtime operations can inherit task attribution
/// without hard-coding any execution-plan knowledge.
/// </summary>
public interface IExecutionTaskScope
{
    /// <summary>
    /// Gets the currently scoped task identifier when the logger is already executing inside a task-specific context.
    /// </summary>
    ExecutionTaskId? CurrentTaskId { get; }
}
