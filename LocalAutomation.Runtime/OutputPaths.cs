using System.IO;

namespace LocalAutomation.Runtime;

/// <summary>
/// Centralizes the shared output-root conventions used by runtime targets and operations.
/// </summary>
public static class OutputPaths
{
    private const string DefaultRootPathValue = @"C:\LocalAutomation";
    private const string DefaultTempRootPathValue = @"C:\LocalAutomation\Temp";
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
    /// Returns the temp workspace root used by one execution session.
    /// </summary>
    public static string GetSessionTempRoot(ExecutionSessionId sessionId)
    {
        return Path.Combine(TempRoot(), "session", ExecutionPathConventions.GetSessionPathId(sessionId));
    }

    /// <summary>
    /// Returns the parent directory that stores all per-session temp workspaces.
    /// </summary>
    public static string GetSessionTempRootParent()
    {
        return Path.Combine(TempRoot(), "session");
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
