using System;
using System.IO;
using System.Linq;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Centralizes the disk locations used for Avalonia launch logs so file-backed diagnostics remain predictable.
/// </summary>
internal static class LoggingPaths
{
    /// <summary>
    /// Gets the host-specific LocalAppData folder for the currently configured launcher.
    /// </summary>
    private static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        App.Branding.DataFolderName);

    /// <summary>
    /// Stores per-launch Avalonia logs next to the shell's persisted state under LocalAppData.
    /// </summary>
    public static string LogsFolder => Path.Combine(DataFolder, "Logs");

    /// <summary>
    /// Creates a unique log file path for the current Avalonia app launch.
    /// </summary>
    public static string CreateLaunchLogFilePath()
    {
        Directory.CreateDirectory(LogsFolder);
        return Path.Combine(LogsFolder, $"{App.Branding.LaunchLogFilePrefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log");
    }

    /// <summary>
    /// Keeps only the newest launch logs so the local log directory stays bounded over time.
    /// </summary>
    public static void CleanupOldLaunchLogs(int maxLogFiles)
    {
        Directory.CreateDirectory(LogsFolder);
        string searchPattern = $"{App.Branding.LaunchLogFilePrefix}_*.log";

        foreach (FileInfo logFile in new DirectoryInfo(LogsFolder)
                     .GetFiles(searchPattern)
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .ThenByDescending(file => file.Name)
                     .Skip(maxLogFiles))
        {
            try
            {
                logFile.Delete();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
