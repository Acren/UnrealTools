using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace LocalAutomation.Application;

/// <summary>
/// Collects detached per-file persisted setting writes so UI callers can capture reflected values once and let a
/// background saver merge and write them later.
/// </summary>
public sealed class PersistedSettingsWriteBatch
{
    private readonly Dictionary<string, PersistedSettingValueCollection> _fileWrites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets whether the batch currently contains any file updates.
    /// </summary>
    public bool IsEmpty => _fileWrites.Count == 0;

    /// <summary>
    /// Gets the per-file key/value patches that should be applied during persistence.
    /// </summary>
    public IReadOnlyDictionary<string, PersistedSettingValueCollection> FileWrites => _fileWrites;

    /// <summary>
    /// Adds or replaces one persisted key write for the provided file path.
    /// </summary>
    public void WriteValue(string filePath, string key, JToken value)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path must be provided.", nameof(filePath));
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Setting key must be provided.", nameof(key));
        }

        if (value == null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (!_fileWrites.TryGetValue(filePath, out PersistedSettingValueCollection? fileValues))
        {
            fileValues = new PersistedSettingValueCollection();
            _fileWrites[filePath] = fileValues;
        }

        fileValues.Values[key] = value.DeepClone();
    }

    /// <summary>
    /// Returns one merged batch where later writes override earlier writes for the same file path and setting key.
    /// </summary>
    public PersistedSettingsWriteBatch Merge(PersistedSettingsWriteBatch laterWrites)
    {
        if (laterWrites == null)
        {
            throw new ArgumentNullException(nameof(laterWrites));
        }

        PersistedSettingsWriteBatch merged = new();
        merged.Apply(this);
        merged.Apply(laterWrites);
        return merged;
    }

    /// <summary>
    /// Applies another batch into the current batch so later file patches replace earlier values for matching keys.
    /// </summary>
    private void Apply(PersistedSettingsWriteBatch laterWrites)
    {
        foreach ((string filePath, PersistedSettingValueCollection fileValues) in laterWrites._fileWrites)
        {
            foreach ((string key, JToken value) in fileValues.Values)
            {
                WriteValue(filePath, key, value);
            }
        }
    }
}
