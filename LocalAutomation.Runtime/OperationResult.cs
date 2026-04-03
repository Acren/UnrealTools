#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Captures the terminal outcome of a completed runtime operation.
/// </summary>
public class OperationResult : RunResult
{
    /// <summary>
    /// Creates an operation result with the provided success state.
    /// </summary>
    public OperationResult(bool success)
        : base(success ? ExecutionTaskOutcome.Completed : ExecutionTaskOutcome.Failed)
    {
    }

    /// <summary>
    /// Creates an operation result with the provided semantic result.
    /// </summary>
    public OperationResult(ExecutionTaskOutcome outcome)
        : base(outcome)
    {
    }

    /// <summary>
    /// Gets or sets the human-readable reason for a failed, cancelled, interrupted, or skipped result when one is known.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Creates a successful operation result.
    /// </summary>
    public static OperationResult Succeeded(int exitCode = 0)
    {
        return new OperationResult(ExecutionTaskOutcome.Completed)
        {
            ExitCode = exitCode
        };
    }

    /// <summary>
    /// Creates a failed operation result.
    /// </summary>
    public static OperationResult Failed(int exitCode = 0, string? failureReason = null)
    {
        return new OperationResult(ExecutionTaskOutcome.Failed)
        {
            ExitCode = exitCode,
            FailureReason = failureReason
        };
    }

    /// <summary>
    /// Creates a cancelled operation result.
    /// </summary>
    public static OperationResult Cancelled(int exitCode = 0, string? failureReason = null)
    {
        return new OperationResult(ExecutionTaskOutcome.Cancelled)
        {
            ExitCode = exitCode,
            FailureReason = failureReason
        };
    }

    /// <summary>
    /// Creates an interrupted operation result.
    /// </summary>
    public static OperationResult Interrupted(int exitCode = 0, string? failureReason = null)
    {
        return new OperationResult(ExecutionTaskOutcome.Interrupted)
        {
            ExitCode = exitCode,
            FailureReason = failureReason
        };
    }
}
