using LocalAutomation.Core;

namespace LocalAutomation.Runtime;

/// <summary>
/// Identifies one declared task inside a plan builder without exposing internal runtime ids to operation code.
/// </summary>
public readonly struct ExecutionTaskHandle
{
    internal ExecutionTaskHandle(ExecutionTaskId id)
    {
        Id = id;
    }

    /// <summary>
    /// Gets whether the handle points at a declared task.
    /// </summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Id.Value);

    /// <summary>
    /// Gets the runtime task identifier represented by this handle.
    /// </summary>
    public ExecutionTaskId Id { get; }
}
