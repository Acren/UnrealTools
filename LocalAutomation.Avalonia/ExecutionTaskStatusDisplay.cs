using System;
using LocalAutomation.Runtime;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Provides shared user-facing text for execution task statuses inside Avalonia surfaces.
/// </summary>
public static class ExecutionTaskStatusDisplay
{
    /// <summary>
    /// Returns the semantic status users should primarily see for a task. Lifecycle state remains available separately so
    /// the UI can still explain whether a failed or cancelled scope is actively unwinding.
    /// </summary>
    public static ExecutionTaskStatus GetDisplayStatus(ExecutionTaskStatus lifecycleStatus, ExecutionTaskStatus? result)
    {
        return result ?? lifecycleStatus;
    }

    public static string GetLabel(ExecutionTaskStatus status)
    {
        return status switch
        {
            ExecutionTaskStatus.Completed => "Done",
            ExecutionTaskStatus.Pending => "Pending",
            ExecutionTaskStatus.Running => "Running",
            ExecutionTaskStatus.Failed => "Failed",
            ExecutionTaskStatus.Skipped => "Skipped",
            ExecutionTaskStatus.Disabled => "Disabled",
            ExecutionTaskStatus.Cancelled => "Cancelled",
            ExecutionTaskStatus.Interrupted => "Interrupted",
            ExecutionTaskStatus.Planned => "Planned",
            _ => status.ToString()
        };
    }

    public static string GetUpperLabel(ExecutionTaskStatus status)
    {
        return GetLabel(status).ToUpperInvariant();
    }
}
