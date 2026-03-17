using System;
using System.IO;
using System.Linq;

namespace UnrealCommander
{
    /// <summary>
    ///     Centralizes the disk locations used for UnrealCommander log files.
    /// </summary>
    internal static class LoggingPaths
    {
        private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealCommander");

        /// <summary>
        ///     Stores per-launch log files next to the app's persisted data under LocalAppData.
        /// </summary>
        public static string LogsFolder => Path.Combine(DataFolder, "Logs");

        /// <summary>
        ///     Creates a unique log file path for the current app launch so each run gets its own file.
        /// </summary>
        public static string CreateLaunchLogFilePath()
        {
            Directory.CreateDirectory(LogsFolder);
            return Path.Combine(LogsFolder, $"UnrealCommander_{DateTime.Now:yyyyMMdd_HHmmss}_{Environment.ProcessId}.log");
        }

        /// <summary>
        ///     Keeps only the newest launch logs so the local log directory stays bounded over time.
        /// </summary>
        public static void CleanupOldLaunchLogs(int maxLogFiles)
        {
            Directory.CreateDirectory(LogsFolder);

            foreach (FileInfo logFile in new DirectoryInfo(LogsFolder)
                         .GetFiles("UnrealCommander_*.log")
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
}
