namespace LocalAutomation.Runtime;

/// <summary>
/// Identifies the storage layer that owns a persisted value.
/// </summary>
public enum PersistenceScope
{
    /// <summary>
    /// Stores a value in the host-wide appdata file shared across targets.
    /// </summary>
    Global,

    /// <summary>
    /// Stores a value beside the target inside the target directory.
    /// </summary>
    TargetLocal,

    /// <summary>
    /// Stores a value in the per-target appdata override layer.
    /// </summary>
    UserTargetOverride
}
