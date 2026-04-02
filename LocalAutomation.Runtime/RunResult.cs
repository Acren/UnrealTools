namespace LocalAutomation.Runtime;

/// <summary>
/// Captures the semantic result of a completed command or operation run.
/// </summary>
public class RunResult
{
    public RunResult(ExecutionTaskStatus result)
    {
        Result = result;
    }

    public ExecutionTaskStatus Result { get; set; }

    public bool Success
    {
        get => Result == ExecutionTaskStatus.Completed;
        set => Result = value ? ExecutionTaskStatus.Completed : ExecutionTaskStatus.Failed;
    }

    public bool WasCancelled => Result == ExecutionTaskStatus.Cancelled;

    public int ExitCode { get; set; }
}
