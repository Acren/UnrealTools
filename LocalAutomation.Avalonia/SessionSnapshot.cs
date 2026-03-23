using System.Collections.Generic;
using LocalAutomation.Application;
using LocalAutomation.Extensions.Abstractions;
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
    public int Version { get; set; } = 2;

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
    /// Gets or sets the selected target key as a typed value for in-memory business logic.
    /// </summary>
    [JsonIgnore]
    public TargetKey? TypedSelectedTargetKey
    {
        get => TargetKey.FromNullable(SelectedTargetKey);
        set => SelectedTargetKey = value?.Value;
    }

    /// <summary>
    /// Gets or sets the in-progress target path text.
    /// </summary>
    [JsonProperty]
    public string PendingTargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the last selected operation identifier for each target type so switching between targets of the
    /// same kind can retain the user's preferred operation.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, string?> SelectedOperationIdsByTargetType { get; set; } = new();

    /// <summary>
    /// Gets the remembered selected operation identifiers keyed by typed target type ids for in-memory business logic.
    /// </summary>
    [JsonIgnore]
    public Dictionary<TargetTypeId, OperationId?> TypedSelectedOperationIdsByTargetType
    {
        get
        {
            Dictionary<TargetTypeId, OperationId?> typedValues = new();
            foreach ((string targetTypeId, string? operationId) in SelectedOperationIdsByTargetType)
            {
                if (string.IsNullOrWhiteSpace(targetTypeId))
                {
                    continue;
                }

                typedValues[new TargetTypeId(targetTypeId)] = OperationId.FromNullable(operationId);
            }

            return typedValues;
        }
        set
        {
            SelectedOperationIdsByTargetType = new Dictionary<string, string?>();
            if (value == null)
            {
                return;
            }

            foreach ((TargetTypeId targetTypeId, OperationId? operationId) in value)
            {
                SelectedOperationIdsByTargetType[targetTypeId.Value] = operationId?.Value;
            }
        }
    }
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
    /// Gets or sets the stable target key as a typed value for in-memory business logic.
    /// </summary>
    [JsonIgnore]
    public TargetKey TypedKey
    {
        get => new(Key);
        set => Key = value.Value;
    }

    /// <summary>
    /// Gets or sets the extension target descriptor identifier.
    /// </summary>
    [JsonProperty]
    public string TargetTypeId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the extension target descriptor identifier as a typed value for in-memory business logic.
    /// </summary>
    [JsonIgnore]
    public TargetTypeId TypedTargetTypeId
    {
        get => new(TargetTypeId);
        set => TargetTypeId = value.Value;
    }

    /// <summary>
    /// Gets or sets the target path used to recreate the runtime target.
    /// </summary>
    [JsonProperty]
    public string Path { get; set; } = string.Empty;
}
