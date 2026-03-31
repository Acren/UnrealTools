using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Accepts runtime status updates for execution-plan tasks.
/// </summary>
public interface IExecutionTaskStateSink
{
    void SetTaskStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null);
}
