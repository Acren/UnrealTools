using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Identifies one declared group inside a plan builder without exposing internal runtime ids to operation code.
/// </summary>
public readonly struct ExecutionGroupHandle
{
    internal ExecutionGroupHandle(ExecutionTaskId id)
    {
        Id = id;
    }

    /// <summary>
    /// Gets whether the handle points at a declared group.
    /// </summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Id.Value);

    internal ExecutionTaskId Id { get; }
}
