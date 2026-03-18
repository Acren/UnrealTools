namespace LocalAutomation.Persistence;

/// <summary>
/// Carries the loaded state object together with whether preprocessing modified the stored JSON during load.
/// </summary>
public sealed class JsonStateLoadResult<TState> where TState : class
{
    /// <summary>
    /// Creates a load result for the provided state object.
    /// </summary>
    public JsonStateLoadResult(TState state, bool wasModified)
    {
        State = state;
        WasModified = wasModified;
    }

    /// <summary>
    /// Gets the loaded state object.
    /// </summary>
    public TState State { get; }

    /// <summary>
    /// Gets whether the JSON document was changed during preprocessing before deserialization.
    /// </summary>
    public bool WasModified { get; }
}
