namespace LocalAutomation.Runtime;

/// <summary>
/// Represents the semantic terminal outcome of an execution task or operation.
/// </summary>
public enum ExecutionTaskOutcome
{
    Completed,
    Failed,
    Skipped,
    Disabled,
    Cancelled,
    Interrupted
}
