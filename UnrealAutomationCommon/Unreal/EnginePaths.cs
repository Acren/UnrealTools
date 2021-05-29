using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public static class EnginePaths
    {
        public static string GetBuildVersionPath(this EngineInstall EngineInstall)
        {
            return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Build", "Build.version");
        }

        public static string GetRunUATPath(this EngineInstall EngineInstall)
        {
            return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Build", "BatchFiles", "RunUAT.bat");
        }

        public static string GetBuildPath(this EngineInstall EngineInstall)
        {
            return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Build", "BatchFiles", "Build.bat");
        }

        public static string GetEditorExe(this EngineInstall EngineInstall, OperationParameters operationParameters)
        {
            return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Binaries", "Win64", operationParameters.Configuration == BuildConfiguration.DebugGame ? "UE4Editor-Win64-DebugGame.exe" : "UE4Editor.exe");
        }

        public static string GetUBTExe(this EngineInstall EngineInstall)
        {
            if (EngineInstall.GetVersion().MajorVersion >= 5)
            {
                return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.exe");
            }
            return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Binaries", "DotNET", "UnrealBuildTool.exe");
        }
    }
}
