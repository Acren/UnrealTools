using System.Collections.Generic;
using Newtonsoft.Json;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Stores the stable, versioned Avalonia session snapshot used for restoring target-scoped UI state.
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public sealed class SessionSnapshot
{
    /// <summary>
    /// Gets or sets the snapshot schema version.
    /// </summary>
    [JsonProperty]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets or sets the persisted targets and their nested UI state.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<TargetSessionSnapshot> Targets { get; set; } = new();

    /// <summary>
    /// Gets or sets the key of the currently selected target.
    /// </summary>
    [JsonProperty]
    public string? SelectedTargetKey { get; set; }

    /// <summary>
    /// Gets or sets the in-progress target path text.
    /// </summary>
    [JsonProperty]
    public string PendingTargetPath { get; set; } = string.Empty;
}

/// <summary>
/// Stores the stable identity and nested UI state for one persisted target.
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public sealed class TargetSessionSnapshot
{
    /// <summary>
    /// Gets or sets the stable target key.
    /// </summary>
    [JsonProperty]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the extension target descriptor identifier.
    /// </summary>
    [JsonProperty]
    public string TargetTypeId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target path used to recreate the runtime target.
    /// </summary>
    [JsonProperty]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the nested per-target UI state.
    /// </summary>
    [JsonProperty]
    public TargetUiStateSnapshot State { get; set; } = new();
}

/// <summary>
/// Stores the target-scoped UI state restored when a target becomes active.
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public sealed class TargetUiStateSnapshot
{
    /// <summary>
    /// Gets or sets the selected operation identifier for the target.
    /// </summary>
    [JsonProperty]
    public string? SelectedOperationId { get; set; }
}
