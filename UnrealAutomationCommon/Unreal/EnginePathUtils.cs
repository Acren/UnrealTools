using System.IO;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    public static class EnginePathUtils
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

        public static string GetExeConfigurationModifier(BuildConfiguration configuration)
        {
            switch (configuration)
            {
                case BuildConfiguration.Debug:
                {
                    return "-Win64-Debug";
                }
                case BuildConfiguration.DebugGame:
                {
                    return "-Win64-DebugGame";
                }
                case BuildConfiguration.Test:
                {
                    return "-Win64-Test";
                }
                case BuildConfiguration.Shipping:
                {
                    return "-Win64-Shipping";
                }
                default:
                {
                    return "";
                }
            }
        }

        public static string GetEditorExe(this EngineInstall EngineInstall, BuildConfiguration configuration, bool Cmd = false)
        {
            string editorExe = EngineInstall.BaseEditorName;

            editorExe += GetExeConfigurationModifier(configuration);

            if (Cmd)
            {
                editorExe += "-Cmd";
            }

            editorExe += ".exe";

            return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Binaries", "Win64", editorExe);
        }

        public static string GetEditorCmdExe(this EngineInstall EngineInstall, BuildConfiguration configuration)
        {
            return GetEditorExe(EngineInstall, configuration, true);
        }

        public static string GetEditorExe(this EngineInstall EngineInstall, OperationParameters operationParameters)
        {
            BuildConfigurationOptions buildOptions = operationParameters.RequestOptions<BuildConfigurationOptions>();
            if (buildOptions == null)
            {
                return null;
            }

            return GetEditorExe(EngineInstall, buildOptions.Configuration);
        }

        public static string GetUBTExe(this EngineInstall EngineInstall)
        {
            if (EngineInstall.Version.MajorVersion >= 5)
            {
                return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.exe");
            }

            return Path.Combine(EngineInstall.InstallDirectory, "Engine", "Binaries", "DotNET", "UnrealBuildTool.exe");
        }
    }
}