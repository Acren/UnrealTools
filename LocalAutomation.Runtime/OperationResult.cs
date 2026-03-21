using LocalAutomation.Core;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Captures the generic success state of a completed runtime operation.
/// </summary>
public class OperationResult : RunResult
{
    /// <summary>
    /// Creates an operation result with the provided success state.
    /// </summary>
    public OperationResult(bool success)
        : base(success)
    {
    }
}
