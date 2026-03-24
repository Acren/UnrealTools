using System.IO;

namespace LocalAutomation.Runtime;

/// <summary>
/// Centralizes the shared output-root conventions used by runtime targets and operations.
/// </summary>
public static class OutputPaths
{
    private const string DefaultRootPathValue = @"C:\UC";
    private static string _rootPath = DefaultRootPathValue;

    /// <summary>
    /// Gets the default root directory used when no host-specific override has been applied.
    /// </summary>
    public static string DefaultRootPath => DefaultRootPathValue;

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
