using System.IO;

namespace LocalAutomation.Runtime;

/// <summary>
/// Centralizes the shared output-root conventions used by runtime targets and operations.
/// </summary>
public static class OutputPaths
{
    /// <summary>
    /// Returns the root directory under which generated automation output is stored.
    /// </summary>
    public static string Root()
    {
        return @"C:\UC\";
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
