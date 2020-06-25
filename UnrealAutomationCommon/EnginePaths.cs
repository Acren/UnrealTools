using System.IO;

namespace UnrealAutomationCommon
{
    public static class EnginePaths
    {
        public static string GetRunUATPath(string EngineInstallDirectory)
        {
            return Path.Combine(EngineInstallDirectory, "Engine", "Build", "BatchFiles", "RunUAT.bat");
        }
    }
}
