using System;
using System.IO;
using System.Linq;
using LocalAutomation.Application;
using LocalAutomation.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Loads, migrates, and saves the Avalonia shell's stable session snapshot so layout or editor refactors do not wipe
/// target-scoped working state.
/// </summary>
public sealed class SessionPersistenceService
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAutomation");

    private static readonly string DataFilePath = Path.Combine(DataFolder, "avalonia-session.json");

    private readonly LocalAutomationApplicationHost _services;

    /// <summary>
    /// Creates a session persistence service around the shared application host.
    /// </summary>
    public SessionPersistenceService(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Loads the persisted snapshot, migrating the legacy live-object session format when necessary.
    /// </summary>
    public SessionSnapshot Load()
    {
        if (!File.Exists(DataFilePath))
        {
            return new SessionSnapshot();
        }

        try
        {
            string jsonText = File.ReadAllText(DataFilePath);
            JToken token = JToken.Parse(jsonText);

            if (token["Version"] != null || token["version"] != null)
            {
                SessionSnapshot? snapshot = token.ToObject<SessionSnapshot>(CreateSnapshotSerializer());
                return snapshot ?? new SessionSnapshot();
            }

            SessionState? legacyState = token.ToObject<SessionState>(CreateLegacySerializer());
            return legacyState == null ? new SessionSnapshot() : MigrateLegacyState(legacyState);
        }
        catch
        {
            return new SessionSnapshot();
        }
    }

    /// <summary>
    /// Saves the provided snapshot using the stable, versioned session format.
    /// </summary>
    public void Save(SessionSnapshot snapshot)
    {
        JsonFileStateStore<SessionSnapshot> store = new(
            filePath: DataFilePath,
            createDefaultState: static () => new SessionSnapshot(),
            createSerializer: CreateSnapshotSerializer);

        store.Save(snapshot);
    }

    /// <summary>
    /// Builds the stable key used to persist target-scoped UI state.
    /// </summary>
    public string BuildTargetKey(string targetTypeId, string targetPath)
    {
        return $"{targetTypeId}|{targetPath}";
    }

    /// <summary>
    /// Creates or updates a persisted target snapshot from the provided runtime target.
    /// </summary>
    public TargetSessionSnapshot CreateTargetSnapshot(object target)
    {
        string targetTypeId = _services.Targets.GetTargetTypeId(target) ?? throw new InvalidOperationException($"No target descriptor matched '{target.GetType().Name}'.");
        string targetPath = _services.Targets.GetTargetPath(target);
        return new TargetSessionSnapshot
        {
            Key = BuildTargetKey(targetTypeId, targetPath),
            TargetTypeId = targetTypeId,
            Path = targetPath,
            State = new TargetUiStateSnapshot()
        };
    }

    /// <summary>
    /// Attempts to recreate a runtime target from its persisted snapshot.
    /// </summary>
    public bool TryRestoreTarget(TargetSessionSnapshot snapshot, out object? target)
    {
        target = null;
        if (!_services.Targets.TryCreateTarget(snapshot.Path, out object? createdTarget) || createdTarget == null)
        {
            return false;
        }

        if (!_services.Targets.IsTarget(createdTarget) || !_services.Targets.IsValidTarget(createdTarget))
        {
            return false;
        }

        string? restoredTypeId = _services.Targets.GetTargetTypeId(createdTarget);
        if (!string.Equals(restoredTypeId, snapshot.TargetTypeId, StringComparison.Ordinal))
        {
            return false;
        }

        target = createdTarget;
        return true;
    }

    /// <summary>
    /// Migrates the legacy live-object session file into the stable nested-per-target snapshot format.
    /// </summary>
    private SessionSnapshot MigrateLegacyState(SessionState legacyState)
    {
        SessionSnapshot snapshot = new()
        {
            PendingTargetPath = legacyState.NewTargetPath
        };

        foreach (object target in legacyState.Targets)
        {
            string? targetTypeId = _services.Targets.GetTargetTypeId(target);
            if (string.IsNullOrWhiteSpace(targetTypeId) || !_services.Targets.IsValidTarget(target))
            {
                continue;
            }

            string targetPath = _services.Targets.GetTargetPath(target);
            snapshot.Targets.Add(new TargetSessionSnapshot
            {
                Key = BuildTargetKey(targetTypeId, targetPath),
                TargetTypeId = targetTypeId,
                Path = targetPath,
                State = new TargetUiStateSnapshot()
            });
        }

        TargetSessionSnapshot? selectedTarget = snapshot.Targets.FirstOrDefault(item =>
            string.Equals(item.Path, legacyState.SelectedTargetPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.TargetTypeId, ResolveLegacyTargetTypeId(legacyState.SelectedTargetTypeName), StringComparison.Ordinal));

        selectedTarget ??= snapshot.Targets.FirstOrDefault();
        if (selectedTarget == null)
        {
            return snapshot;
        }

        selectedTarget.State.SelectedOperationId = legacyState.OperationType == null
            ? null
            : _services.Operations.GetOperation(legacyState.OperationType)?.Id;

        selectedTarget.State.OptionValues = _services.OptionValues.Capture(legacyState.OptionsInstances.Cast<object>());
        snapshot.SelectedTargetKey = selectedTarget.Key;
        return snapshot;
    }

    /// <summary>
    /// Resolves the target descriptor identifier from the legacy runtime type name.
    /// </summary>
    private string? ResolveLegacyTargetTypeId(string? legacyTypeName)
    {
        if (string.IsNullOrWhiteSpace(legacyTypeName))
        {
            return null;
        }

        return _services.Catalog.TargetDescriptors.FirstOrDefault(descriptor => string.Equals(descriptor.TargetType.FullName, legacyTypeName, StringComparison.Ordinal))?.Id;
    }

    /// <summary>
    /// Creates the Json.NET serializer used for the stable session snapshot.
    /// </summary>
    private static JsonSerializer CreateSnapshotSerializer()
    {
        return new JsonSerializer();
    }

    /// <summary>
    /// Reuses the old serializer settings so the legacy live-object state can still be deserialized for one-way
    /// migration.
    /// </summary>
    private static JsonSerializer CreateLegacySerializer()
    {
        return new JsonSerializer
        {
            PreserveReferencesHandling = PreserveReferencesHandling.All,
            TypeNameHandling = TypeNameHandling.Auto,
            SerializationBinder = new DefaultSerializationBinder()
        };
    }
}
