namespace LocalAutomation.Core;

/// <summary>
/// Represents the terminal outcome of a command or operation run.
/// </summary>
public enum RunOutcome
{
    /// <summary>
    /// The run completed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// The run completed unsuccessfully.
    /// </summary>
    Failed,

    /// <summary>
    /// The run was cancelled before it completed.
    /// </summary>
    Cancelled
}
