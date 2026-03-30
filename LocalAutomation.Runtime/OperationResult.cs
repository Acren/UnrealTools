using LocalAutomation.Core;

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
        : base(success ? RunOutcome.Succeeded : RunOutcome.Failed)
    {
    }

    /// <summary>
    /// Creates an operation result with the provided terminal outcome.
    /// </summary>
    public OperationResult(RunOutcome outcome)
        : base(outcome)
    {
    }

    /// <summary>
    /// Creates a successful operation result.
    /// </summary>
    public static OperationResult Succeeded(int exitCode = 0)
    {
        return new OperationResult(RunOutcome.Succeeded)
        {
            ExitCode = exitCode
        };
    }

    /// <summary>
    /// Creates a failed operation result.
    /// </summary>
    public static OperationResult Failed(int exitCode = 0)
    {
        return new OperationResult(RunOutcome.Failed)
        {
            ExitCode = exitCode
        };
    }

    /// <summary>
    /// Creates a cancelled operation result.
    /// </summary>
    public static OperationResult Cancelled(int exitCode = 0)
    {
        return new OperationResult(RunOutcome.Cancelled)
        {
            ExitCode = exitCode
        };
    }
}
