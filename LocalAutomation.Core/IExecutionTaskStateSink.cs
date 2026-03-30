namespace LocalAutomation.Core;

/// <summary>
/// Accepts runtime status updates for execution-plan tasks so preview graphs can transition into live execution state
/// without the UI inferring progress from log text.
/// </summary>
public interface IExecutionTaskStateSink
{
    /// <summary>
    /// Updates the runtime status for the provided execution task.
    /// </summary>
    void SetTaskStatus(ExecutionTaskId taskId, ExecutionTaskStatus status, string? statusReason = null);
}
