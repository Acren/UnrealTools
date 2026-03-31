using System;
using LocalAutomation.Runtime;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Provides shared user-facing text for execution task statuses inside Avalonia surfaces.
/// </summary>
public static class ExecutionTaskStatusDisplay
{
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
            ExecutionTaskStatus.Planned => "Planned",
            _ => status.ToString()
        };
    }

    public static string GetUpperLabel(ExecutionTaskStatus status)
    {
        return GetLabel(status).ToUpperInvariant();
    }
}
