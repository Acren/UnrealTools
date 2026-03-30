namespace LocalAutomation.Core;

/// <summary>
/// Captures the terminal outcome of a completed command or operation run.
/// </summary>
public class RunResult
{
    /// <summary>
    /// Creates a run result with the provided outcome.
    /// </summary>
    public RunResult(RunOutcome outcome)
    {
        Outcome = outcome;
    }

    /// <summary>
    /// Gets or sets the terminal outcome of the run.
    /// </summary>
    public RunOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets whether the run completed successfully. This remains as a compatibility shim while older callers
    /// migrate to the richer outcome model.
    /// </summary>
    public bool Success
    {
        get => Outcome == RunOutcome.Succeeded;
        set => Outcome = value ? RunOutcome.Succeeded : RunOutcome.Failed;
    }

    /// <summary>
    /// Gets whether the run ended in cancellation.
    /// </summary>
    public bool WasCancelled => Outcome == RunOutcome.Cancelled;

    /// <summary>
    /// Gets or sets the exit code reported by the executed process when one exists.
    /// </summary>
    public int ExitCode { get; set; }
}
