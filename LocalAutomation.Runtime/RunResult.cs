namespace LocalAutomation.Runtime;

/// <summary>
/// Captures the terminal outcome of a completed command or operation run.
/// </summary>
public class RunResult
{
    public RunResult(RunOutcome outcome)
    {
        Outcome = outcome;
    }

    public RunOutcome Outcome { get; set; }

    public bool Success
    {
        get => Outcome == RunOutcome.Succeeded;
        set => Outcome = value ? RunOutcome.Succeeded : RunOutcome.Failed;
    }

    public bool WasCancelled => Outcome == RunOutcome.Cancelled;

    public int ExitCode { get; set; }
}
