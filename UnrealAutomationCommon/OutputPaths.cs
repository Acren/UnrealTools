using System.IO;

namespace UnrealAutomationCommon
{
    public static class OutputPaths
    {
        public static string Root()
        {
            return global::LocalAutomation.Runtime.OutputPaths.Root();
        }

        public static string GetTestReportPath(string OutputPath)
        {
            return Path.Combine(OutputPath, "TestReport");
        }

        public static string GetTestReportFilePath(string OutputPath)
        {
            return Path.Combine(GetTestReportPath(OutputPath), "index.json");
        }
    }
}
