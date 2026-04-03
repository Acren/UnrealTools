namespace LocalAutomation.Runtime;

/// <summary>
/// Captures the semantic result of a completed command or operation run.
/// </summary>
public class RunResult
{
    public RunResult(ExecutionTaskOutcome outcome)
    {
        Outcome = outcome;
    }

    public ExecutionTaskOutcome Outcome { get; set; }

    public bool Success
    {
        get => Outcome == ExecutionTaskOutcome.Completed;
        set => Outcome = value ? ExecutionTaskOutcome.Completed : ExecutionTaskOutcome.Failed;
    }

    public bool WasCancelled => Outcome == ExecutionTaskOutcome.Cancelled;

    public int ExitCode { get; set; }
}
