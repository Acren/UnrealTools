using System;
using LocalAutomation.Runtime;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Represents the single visual status language Avalonia surfaces render after combining runtime state and outcome.
/// </summary>
public enum ExecutionTaskDisplayStatus
{
    Planned,
    Queued,
    AwaitingDependency,
    AwaitingLock,
    Running,
    Completed,
    Failed,
    Skipped,
    Disabled,
    Cancelled,
    Interrupted
}

/// <summary>
/// Provides shared user-facing text for execution task states inside Avalonia surfaces.
/// </summary>
public static class ExecutionTaskStatusDisplay
{
    /// <summary>
    /// Returns the semantic status users should primarily see for a task. Runtime state remains available separately so
    /// the UI can still explain whether a failed or cancelled scope is actively unwinding.
    /// </summary>
    public static ExecutionTaskDisplayStatus GetDisplayStatus(ExecutionTaskState state, ExecutionTaskOutcome? outcome)
    {
        if (outcome != null)
        {
            return outcome.Value switch
            {
                ExecutionTaskOutcome.Completed => ExecutionTaskDisplayStatus.Completed,
                ExecutionTaskOutcome.Failed => ExecutionTaskDisplayStatus.Failed,
                ExecutionTaskOutcome.Skipped => ExecutionTaskDisplayStatus.Skipped,
                ExecutionTaskOutcome.Disabled => ExecutionTaskDisplayStatus.Disabled,
                ExecutionTaskOutcome.Cancelled => ExecutionTaskDisplayStatus.Cancelled,
                ExecutionTaskOutcome.Interrupted => ExecutionTaskDisplayStatus.Interrupted,
                _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
            };
        }

        return state switch
        {
            ExecutionTaskState.Planned => ExecutionTaskDisplayStatus.Planned,
            ExecutionTaskState.Queued => ExecutionTaskDisplayStatus.Queued,
            ExecutionTaskState.AwaitingDependency => ExecutionTaskDisplayStatus.AwaitingDependency,
            ExecutionTaskState.AwaitingLock => ExecutionTaskDisplayStatus.AwaitingLock,
            ExecutionTaskState.Running => ExecutionTaskDisplayStatus.Running,
            ExecutionTaskState.Completed => ExecutionTaskDisplayStatus.Completed,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }

    /// <summary>
    /// Returns the short user-facing label shown for a display status in task lists and graph surfaces.
    /// </summary>
    public static string GetLabel(ExecutionTaskDisplayStatus status)
    {
        return status switch
        {
            ExecutionTaskDisplayStatus.Completed => "Done",
            ExecutionTaskDisplayStatus.Queued => "Queued",
            ExecutionTaskDisplayStatus.AwaitingDependency => "Awaiting dependency",
            ExecutionTaskDisplayStatus.AwaitingLock => "Awaiting lock",
            ExecutionTaskDisplayStatus.Running => "Running",
            ExecutionTaskDisplayStatus.Failed => "Failed",
            ExecutionTaskDisplayStatus.Skipped => "Skipped",
            ExecutionTaskDisplayStatus.Disabled => "Disabled",
            ExecutionTaskDisplayStatus.Cancelled => "Cancelled",
            ExecutionTaskDisplayStatus.Interrupted => "Interrupted",
            ExecutionTaskDisplayStatus.Planned => "Planned",
            _ => status.ToString()
        };
    }

    public static string GetUpperLabel(ExecutionTaskDisplayStatus status)
    {
        return GetLabel(status).ToUpperInvariant();
    }
}
