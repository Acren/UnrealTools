using System;

namespace LocalAutomation.Core;

/// <summary>
/// Provides the shared user-facing text for execution task statuses so graph nodes, indicators, and other UI surfaces
/// do not duplicate label mappings.
/// </summary>
public static class ExecutionTaskStatusDisplay
{
    /// <summary>
    /// Returns the shared title-case label for one execution task status.
    /// </summary>
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

    /// <summary>
    /// Returns the shared uppercase label for compact graph badges.
    /// </summary>
    public static string GetUpperLabel(ExecutionTaskStatus status)
    {
        return GetLabel(status).ToUpperInvariant();
    }
}
