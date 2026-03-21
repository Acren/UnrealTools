using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Persists the legacy live-object Avalonia session shape so newer builds can migrate old local data into the stable
/// snapshot model without keeping generic shell code coupled to Unreal runtime types.
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public sealed class SessionState
{
    /// <summary>
    /// Gets or sets the persisted legacy runtime targets restored during one-way migration.
    /// </summary>
    [JsonProperty]
    public ObservableCollection<object> Targets { get; set; } = new();

    /// <summary>
    /// Gets or sets the persisted live option instances backing the legacy Avalonia option edits.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<object> OptionsInstances { get; set; } = new();

    /// <summary>
    /// Gets or sets the currently selected runtime operation type.
    /// </summary>
    [JsonProperty]
    public Type? OperationType { get; set; }

    /// <summary>
    /// Gets or sets the most recent freeform target-path input so the shell can reopen in the same editing context.
    /// </summary>
    [JsonProperty]
    public string NewTargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path of the selected target so selection can be restored after deserialization.
    /// </summary>
    [JsonProperty]
    public string? SelectedTargetPath { get; set; }

    /// <summary>
    /// Gets or sets the runtime type name of the selected target so path collisions across target kinds still restore
    /// the correct selection.
    /// </summary>
    [JsonProperty]
    public string? SelectedTargetTypeName { get; set; }
}
