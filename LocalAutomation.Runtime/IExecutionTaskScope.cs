using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Exposes the current task scope carried by a logger.
/// </summary>
public interface IExecutionTaskScope
{
    ExecutionTaskId? CurrentTaskId { get; }
}
