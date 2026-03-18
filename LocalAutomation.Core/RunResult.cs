namespace LocalAutomation.Core;

/// <summary>
/// Captures the generic success state of a completed command or operation run.
/// </summary>
public class RunResult
{
    /// <summary>
    /// Creates a run result with the provided success state.
    /// </summary>
    public RunResult(bool success)
    {
        Success = success;
    }

    /// <summary>
    /// Gets or sets whether the run completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the exit code reported by the executed process when one exists.
    /// </summary>
    public int ExitCode { get; set; }
}
