using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Accepts runtime execution-state updates for execution-plan tasks.
/// </summary>
public interface IExecutionTaskStateSink
{
    void SetTaskState(ExecutionTaskId taskId, ExecutionTaskState state, string? statusReason = null);
}
