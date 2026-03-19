using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Persists the Avalonia shell's lightweight session state so the parity shell can restore targets and selection
/// between launches without depending on the legacy WPF persistence model.
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public sealed class SessionState
{
    /// <summary>
    /// Gets or sets the persisted targets restored into the Avalonia shell.
    /// </summary>
    [JsonProperty]
    public ObservableCollection<IOperationTarget> Targets { get; set; } = new();

    /// <summary>
    /// Gets or sets the persisted additional arguments backing the current Avalonia selection.
    /// </summary>
    [JsonProperty]
    public string AdditionalArguments { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the persisted live option instances backing the current Avalonia option edits.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<OperationOptions> OptionsInstances { get; set; } = new();

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

    /// <summary>
    /// Removes invalid targets after deserialization so stale paths do not keep breaking later startups.
    /// </summary>
    [OnDeserialized]
    internal void OnDeserialized(StreamingContext context)
    {
        List<IOperationTarget> invalidTargets = new();
        foreach (IOperationTarget target in Targets)
        {
            if (!target.IsValid)
            {
                invalidTargets.Add(target);
            }
        }

        foreach (IOperationTarget target in invalidTargets)
        {
            Targets.Remove(target);
        }
    }
}
