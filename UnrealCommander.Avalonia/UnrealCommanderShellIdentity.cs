using LocalAutomation.Avalonia.Bootstrap;

namespace UnrealCommander.Avalonia;

/// <summary>
/// Centralizes the Unreal Commander launcher identity so process metadata, window title, logs, and session files all
/// use the same shell values.
/// </summary>
internal static class UnrealCommanderShellIdentity
{
    /// <summary>
    /// Gets the shell identity used by the Unreal Commander launcher.
    /// </summary>
    public static ShellIdentity Instance { get; } = new(
        applicationName: "Unreal Commander",
        windowTitle: "Unreal Commander",
        dataFolderName: "UnrealCommander",
        targetSettingsFileName: ".ucmdr.json",
        sessionFileName: "session.json",
        launchLogFilePrefix: "UnrealCommander.Avalonia",
        loggerCategoryName: "UnrealCommander.Avalonia",
        defaultOutputRootPath: @"C:\UC",
        defaultTempRootPath: @"C:\UC\Temp");
}
