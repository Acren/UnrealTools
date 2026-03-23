using System;
using System.IO;
using System.Linq;
using LocalAutomation.Application;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Persistence;
using LocalAutomation.Runtime;
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
    private readonly LocalAutomationApplicationHost _services;
    private readonly string _dataFilePath;

    /// <summary>
    /// Creates a session persistence service around the shared application host.
    /// </summary>
    public SessionPersistenceService(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        // Keep each launcher's persisted shell state inside its own LocalAppData root so branded hosts do not overwrite
        // one another's target lists, selected operations, or option values.
        string dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            App.Branding.DataFolderName);
        _dataFilePath = Path.Combine(dataFolder, App.Branding.SessionFileName);
    }

    /// <summary>
    /// Loads the persisted snapshot, migrating the legacy live-object session format when necessary.
    /// </summary>
    public SessionSnapshot Load()
    {
        if (!File.Exists(_dataFilePath))
        {
            return new SessionSnapshot();
        }

        try
        {
            string jsonText = File.ReadAllText(_dataFilePath);
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
            filePath: _dataFilePath,
            createDefaultState: static () => new SessionSnapshot(),
            createSerializer: CreateSnapshotSerializer);

        store.Save(snapshot);
    }

    /// <summary>
    /// Builds the stable key used to persist target-scoped UI state.
    /// </summary>
    public TargetKey BuildTargetKey(TargetTypeId targetTypeId, string targetPath)
    {
        return TargetKeyUtility.BuildTargetKey(targetTypeId, targetPath);
    }

    /// <summary>
    /// Builds the stable key used to persist target-scoped UI state from serialized values.
    /// </summary>
    public string BuildTargetKey(string targetTypeId, string targetPath)
    {
        return BuildTargetKey(new TargetTypeId(targetTypeId), targetPath).Value;
    }

    /// <summary>
    /// Creates or updates a persisted target snapshot from the provided runtime target.
    /// </summary>
    public TargetSessionSnapshot CreateTargetSnapshot(IOperationTarget target)
    {
        TargetTypeId targetTypeId = _services.Targets.GetTargetTypeId(target) ?? throw new InvalidOperationException($"No target descriptor matched '{target.GetType().Name}'.");
        string targetPath = _services.Targets.GetTargetPath(target);
        return new TargetSessionSnapshot
        {
            Key = BuildTargetKey(targetTypeId, targetPath).Value,
            TargetTypeId = targetTypeId.Value,
            Path = targetPath
        };
    }

    /// <summary>
    /// Attempts to recreate a runtime target from its persisted snapshot.
    /// </summary>
    public bool TryRestoreTarget(TargetSessionSnapshot snapshot, out IOperationTarget? target)
    {
        target = null;
        if (!_services.Targets.TryCreateTarget(snapshot.Path, out IOperationTarget? createdTarget) || createdTarget == null)
        {
            return false;
        }

        if (!_services.Targets.IsTarget(createdTarget) || !_services.Targets.IsValidTarget(createdTarget))
        {
            return false;
        }

        TargetTypeId? restoredTypeId = _services.Targets.GetTargetTypeId(createdTarget);
        if (restoredTypeId == null || restoredTypeId.Value != snapshot.TypedTargetTypeId)
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

        foreach (IOperationTarget target in legacyState.Targets.OfType<IOperationTarget>())
        {
            TargetTypeId? targetTypeId = _services.Targets.GetTargetTypeId(target);
            if (targetTypeId == null || !_services.Targets.IsValidTarget(target))
            {
                continue;
            }

            string targetPath = _services.Targets.GetTargetPath(target);
            snapshot.Targets.Add(new TargetSessionSnapshot
            {
                Key = BuildTargetKey(targetTypeId.Value, targetPath).Value,
                TargetTypeId = targetTypeId.Value.Value,
                Path = targetPath
            });
        }

        TargetSessionSnapshot? selectedTarget = snapshot.Targets.FirstOrDefault(item =>
            string.Equals(item.Path, legacyState.SelectedTargetPath, StringComparison.OrdinalIgnoreCase) &&
            item.TypedTargetTypeId == ResolveLegacyTargetTypeId(legacyState.SelectedTargetTypeName));

        selectedTarget ??= snapshot.Targets.FirstOrDefault();
        if (selectedTarget == null)
        {
            return snapshot;
        }

        Dictionary<TargetTypeId, OperationId?> typedSelections = snapshot.TypedSelectedOperationIdsByTargetType;
        typedSelections[selectedTarget.TypedTargetTypeId] = legacyState.OperationType == null
            ? null
            : _services.Operations.GetOperation(legacyState.OperationType)?.Id;
        snapshot.TypedSelectedOperationIdsByTargetType = typedSelections;

        // Migrate legacy option values into the new layered setting files for the selected target so existing user
        // edits are preserved when upgrading from the old session-owned persistence model.
        IOperationTarget? selectedRuntimeTarget = legacyState.Targets.OfType<IOperationTarget>().FirstOrDefault(item =>
            string.Equals(_services.Targets.GetTargetPath(item), selectedTarget.Path, StringComparison.OrdinalIgnoreCase) &&
            _services.Targets.GetTargetTypeId(item) == selectedTarget.TypedTargetTypeId);
        selectedRuntimeTarget ??= legacyState.Targets.OfType<IOperationTarget>().FirstOrDefault();
        if (selectedRuntimeTarget != null)
        {
            TargetSettingsContext context = _services.OptionValues.CreateTargetContext(_services.Targets, selectedRuntimeTarget);
            _services.OptionValues.SaveOptionValues(legacyState.OptionsInstances.Cast<object>(), context);
        }

        snapshot.SelectedTargetKey = selectedTarget.Key;
        return snapshot;
    }

    /// <summary>
    /// Resolves the target descriptor identifier from the legacy runtime type name.
    /// </summary>
    private TargetTypeId? ResolveLegacyTargetTypeId(string? legacyTypeName)
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
