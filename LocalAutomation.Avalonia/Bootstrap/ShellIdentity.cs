using System;
using System.IO;

namespace LocalAutomation.Avalonia.Bootstrap;

/// <summary>
/// Describes the launcher-controlled identity values that let the shared shell present different product
/// names, data folders, and log streams without duplicating the UI implementation.
/// </summary>
public sealed class ShellIdentity
{
    /// <summary>
    /// Gets the default shell identity used by the generic LocalAutomation launcher.
    /// </summary>
    public static ShellIdentity LocalAutomation { get; } = new(
        applicationName: "LocalAutomation",
        windowTitle: "LocalAutomation",
        dataFolderName: "LocalAutomation",
        targetSettingsFileName: ".localautomation.json",
        sessionFileName: "session.json",
        launchLogFilePrefix: "LocalAutomation.Avalonia",
        loggerCategoryName: "LocalAutomation.Avalonia",
        defaultOutputRootPath: @"C:\LocalAutomation",
        defaultTempRootPath: Path.Combine(Path.GetTempPath(), "LocalAutomation"));

    /// <summary>
    /// Creates a shell identity descriptor for one launcher executable.
    /// </summary>
    public ShellIdentity(
        string applicationName,
        string windowTitle,
        string dataFolderName,
        string targetSettingsFileName,
        string sessionFileName,
        string launchLogFilePrefix,
        string loggerCategoryName,
        string defaultOutputRootPath,
        string defaultTempRootPath)
    {
        ApplicationName = Validate(applicationName, nameof(applicationName));
        WindowTitle = Validate(windowTitle, nameof(windowTitle));
        DataFolderName = Validate(dataFolderName, nameof(dataFolderName));
        TargetSettingsFileName = Validate(targetSettingsFileName, nameof(targetSettingsFileName));
        SessionFileName = Validate(sessionFileName, nameof(sessionFileName));
        LaunchLogFilePrefix = Validate(launchLogFilePrefix, nameof(launchLogFilePrefix));
        LoggerCategoryName = Validate(loggerCategoryName, nameof(loggerCategoryName));
        DefaultOutputRootPath = Validate(defaultOutputRootPath, nameof(defaultOutputRootPath));
        DefaultTempRootPath = Validate(defaultTempRootPath, nameof(defaultTempRootPath));
    }

    /// <summary>
    /// Gets the human-readable product name shown in user-facing shell text.
    /// </summary>
    public string ApplicationName { get; }

    /// <summary>
    /// Gets the desktop window title used for the main shell window.
    /// </summary>
    public string WindowTitle { get; }

    /// <summary>
    /// Gets the LocalAppData child folder that stores session state and launch logs for this host.
    /// </summary>
    public string DataFolderName { get; }

    /// <summary>
    /// Gets the repo-local layered settings filename used by this host.
    /// </summary>
    public string TargetSettingsFileName { get; }

    /// <summary>
    /// Gets the session-state filename used by this host inside its LocalAppData folder.
    /// </summary>
    public string SessionFileName { get; }

    /// <summary>
    /// Gets the per-launch file prefix used when writing persistent shell logs.
    /// </summary>
    public string LaunchLogFilePrefix { get; }

    /// <summary>
    /// Gets the MEL logger category name used for file-backed application logs.
    /// </summary>
    public string LoggerCategoryName { get; }

    /// <summary>
    /// Gets the default output root path used by this host when no persisted override exists.
    /// </summary>
    public string DefaultOutputRootPath { get; }

    /// <summary>
    /// Gets the default temp root path used by this host when no persisted override exists.
    /// </summary>
    public string DefaultTempRootPath { get; }

    /// <summary>
    /// Rejects empty launcher identity values so a host never partially configures the shared shell.
    /// </summary>
    private static string Validate(string value, string parameterName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Shell identity values must not be empty.", parameterName)
            : value;
    }
}
