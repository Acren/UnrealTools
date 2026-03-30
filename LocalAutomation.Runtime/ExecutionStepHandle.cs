namespace LocalAutomation.Runtime;

/// <summary>
/// Identifies one declared step inside a plan builder without exposing internal runtime ids to operation code.
/// </summary>
public readonly struct ExecutionStepHandle
{
    internal ExecutionStepHandle(string id)
    {
        Id = id;
    }

    /// <summary>
    /// Gets whether the handle points at a declared step.
    /// </summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Id);

    internal string Id { get; }
}
