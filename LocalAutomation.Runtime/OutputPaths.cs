using System.Collections.Generic;
using System.IO;

namespace LocalAutomation.Runtime;

/// <summary>
/// Centralizes the shared output-root conventions used by runtime targets and operations.
/// </summary>
public static class OutputPaths
{
    private const string DefaultRootPathValue = @"C:\LocalAutomation";
    private const string DefaultTempRootPathValue = @"C:\LocalAutomation\Temp";
    // Serializes temp-slot allocation so concurrent sessions never reserve the same integer folder.
    private static readonly object SessionTempSlotLock = new();
    // Tracks live or quarantined temp slots that must not be reused during this process lifetime.
    private static readonly HashSet<int> ActiveSessionTempSlots = new();
    private static string _rootPath = DefaultRootPathValue;
    private static string _tempRootPath = GetDefaultTempRootPath();

    /// <summary>
    /// Gets the default root directory used when no host-specific override has been applied.
    /// </summary>
    public static string DefaultRootPath => DefaultRootPathValue;

    /// <summary>
    /// Gets the default root directory used for temporary run workspaces when no host-specific override has been applied.
    /// </summary>
    public static string DefaultTempRootPath => GetDefaultTempRootPath();

    /// <summary>
    /// Gets the default temp-root path.
    /// </summary>
    public static string GetDefaultTempRootPath(string? hostDataFolderName = null)
    {
        return DefaultTempRootPathValue;
    }

    /// <summary>
    /// Returns the root directory under which generated automation output is stored.
    /// </summary>
    public static string Root()
    {
        return _rootPath;
    }

    /// <summary>
    /// Applies the host-wide output root override used by runtime targets and operations.
    /// </summary>
    public static void SetRoot(string? rootPath)
    {
        _rootPath = string.IsNullOrWhiteSpace(rootPath) ? DefaultRootPathValue : rootPath.Trim();
    }

    /// <summary>
    /// Returns the root directory under which temporary run workspaces and other scratch files are stored.
    /// </summary>
    public static string TempRoot()
    {
        return _tempRootPath;
    }

    /// <summary>
    /// Applies the host-wide temporary root override used by runtime operations.
    /// </summary>
    public static void SetTempRoot(string? tempRootPath)
    {
        _tempRootPath = string.IsNullOrWhiteSpace(tempRootPath) ? GetDefaultTempRootPath() : tempRootPath.Trim();
    }

    /// <summary>
    /// Returns the parent directory that stores all per-session temp workspaces.
    /// </summary>
    public static string GetSessionTempRootParent()
    {
        return Path.Combine(TempRoot(), "session");
    }

    /// <summary>
    /// Returns the temp workspace root used by one reusable execution-session slot.
    /// </summary>
    public static string GetSessionTempRoot(ExecutionSessionTempSlot slot)
    {
        return Path.Combine(GetSessionTempRootParent(), slot.Value.ToString());
    }

    /// <summary>
    /// Reserves the lowest available integer temp slot for a live execution session.
    /// </summary>
    public static ExecutionSessionTempSlot AllocateSessionTempSlot()
    {
        lock (SessionTempSlotLock)
        {
            string sessionsRootPath = GetSessionTempRootParent();
            Directory.CreateDirectory(sessionsRootPath);

            for (int slotValue = 1; ; slotValue++)
            {
                if (ActiveSessionTempSlots.Contains(slotValue))
                {
                    continue;
                }

                ExecutionSessionTempSlot slot = new(slotValue);
                string slotPath = GetSessionTempRoot(slot);
                if (Directory.Exists(slotPath) && !TryDeleteSessionTempRoot(slotPath))
                {
                    continue;
                }

                Directory.CreateDirectory(slotPath);
                ActiveSessionTempSlots.Add(slotValue);
                return slot;
            }
        }
    }

    /// <summary>
    /// Releases a session temp slot after its workspace has been removed or declared reusable by the caller.
    /// </summary>
    public static void ReleaseSessionTempSlot(ExecutionSessionTempSlot slot)
    {
        lock (SessionTempSlotLock)
        {
            ActiveSessionTempSlots.Remove(slot.Value);
        }
    }

    /// <summary>
    /// Attempts to remove one stale session temp root so its integer slot can be reused safely.
    /// </summary>
    private static bool TryDeleteSessionTempRoot(string sessionTempRootPath)
    {
        try
        {
            Directory.Delete(sessionTempRootPath, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the directory used for exported automation test reports for a specific operation run.
    /// </summary>
    public static string GetTestReportPath(string outputPath)
    {
        return Path.Combine(outputPath, "TestReport");
    }

    /// <summary>
    /// Returns the default exported JSON report file path for a specific operation run.
    /// </summary>
    public static string GetTestReportFilePath(string outputPath)
    {
        return Path.Combine(GetTestReportPath(outputPath), "index.json");
    }
}
