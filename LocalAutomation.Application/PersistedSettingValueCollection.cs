using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalAutomation.Application;

/// <summary>
/// Stores stable persisted values keyed by generated or explicit setting identifiers.
/// </summary>
[JsonObject(MemberSerialization.OptIn)]
public sealed class PersistedSettingValueCollection
{
    /// <summary>
    /// Gets or sets the schema version for this persisted value file.
    /// </summary>
    [JsonProperty]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets or sets the persisted values keyed by stable setting id.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, JToken> Values { get; set; } = new();
}
