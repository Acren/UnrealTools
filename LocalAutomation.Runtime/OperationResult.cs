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
    public static OperationResult Failed(int exitCode = 0)
    {
        return new OperationResult(ExecutionTaskOutcome.Failed)
        {
            ExitCode = exitCode
        };
    }

    /// <summary>
    /// Creates a cancelled operation result.
    /// </summary>
    public static OperationResult Cancelled(int exitCode = 0)
    {
        return new OperationResult(ExecutionTaskOutcome.Cancelled)
        {
            ExitCode = exitCode
        };
    }

    /// <summary>
    /// Creates an interrupted operation result.
    /// </summary>
    public static OperationResult Interrupted(int exitCode = 0)
    {
        return new OperationResult(ExecutionTaskOutcome.Interrupted)
        {
            ExitCode = exitCode
        };
    }
}
