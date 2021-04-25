using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public static class EnginePaths
    {
        public static string GetRunUATPath(string EngineInstallDirectory)
        {
            return Path.Combine(EngineInstallDirectory, "Engine", "Build", "BatchFiles", "RunUAT.bat");
        }

        public static string GetBuildPath(string EngineInstallDirectory)
        {
            return Path.Combine(EngineInstallDirectory, "Engine", "Build", "BatchFiles", "Build.bat");
        }

        public static string GetEditorExe(string EngineInstallDirectory, OperationParameters operationParameters)
        {
            return Path.Combine(EngineInstallDirectory, "Engine", "Binaries", "Win64", operationParameters.Configuration == BuildConfiguration.DebugGame ? "UE4Editor-Win64-DebugGame.exe" : "UE4Editor.exe");
        }
    }
}
