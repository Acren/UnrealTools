using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using LocalAutomation.Persistence;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Provides a dedicated JSON-backed session store for the Avalonia shell so parity state can be restored without
/// coupling the new app to the legacy WPF persistence container.
/// </summary>
public static class SessionStateStore
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalAutomation");

    private static readonly string DataFilePath = Path.Combine(DataFolder, "avalonia-session.json");

    private static readonly JsonFileStateStore<SessionState> Store = new(
        filePath: DataFilePath,
        createDefaultState: static () => new SessionState(),
        createSerializer: CreateSerializer);

    /// <summary>
    /// Loads the last persisted Avalonia session state or a default state when none exists yet.
    /// </summary>
    public static SessionState Load()
    {
        return Store.Load().State;
    }

    /// <summary>
    /// Saves the current Avalonia session state.
    /// </summary>
    public static void Save(SessionState state)
    {
        Store.Save(state);
    }

    /// <summary>
    /// Reuses the legacy Json.NET type metadata settings so persisted Unreal targets and option sets deserialize the
    /// same way they do in the WPF shell.
    /// </summary>
    private static JsonSerializer CreateSerializer()
    {
        return new JsonSerializer
        {
            PreserveReferencesHandling = PreserveReferencesHandling.All,
            TypeNameHandling = TypeNameHandling.Auto,
            SerializationBinder = new DefaultSerializationBinder()
        };
    }
}
