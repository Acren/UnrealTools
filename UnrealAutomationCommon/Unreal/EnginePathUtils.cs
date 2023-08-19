using System.IO;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    public static class EnginePathUtils
    {
        public static string GetBuildVersionPath(this Engine engine)
        {
            return Path.Combine(engine.TargetPath, "Engine", "Build", "Build.version");
        }

        public static string GetRunUATPath(this Engine engine)
        {
            return Path.Combine(engine.TargetPath, "Engine", "Build", "BatchFiles", "RunUAT.bat");
        }

        public static string GetBuildPath(this Engine engine)
        {
            return Path.Combine(engine.TargetPath, "Engine", "Build", "BatchFiles", "Build.bat");
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

        public static string GetEditorExe(this Engine engine, BuildConfiguration configuration, bool Cmd = false)
        {
            string editorExe = engine.BaseEditorName;

            editorExe += GetExeConfigurationModifier(configuration);

            if (Cmd)
            {
                editorExe += "-Cmd";
            }

            editorExe += ".exe";

            return Path.Combine(engine.TargetPath, "Engine", "Binaries", "Win64", editorExe);
        }

        public static string GetEditorCmdExe(this Engine engine, BuildConfiguration configuration)
        {
            return GetEditorExe(engine, configuration, true);
        }

        public static string GetEditorExe(this Engine engine, OperationParameters operationParameters)
        {
            BuildConfigurationOptions buildOptions = operationParameters.RequestOptions<BuildConfigurationOptions>();
            if (buildOptions == null)
            {
                return null;
            }

            return GetEditorExe(engine, buildOptions.Configuration);
        }

        public static string GetUBTExe(this Engine engine)
        {
            if (engine.Version.MajorVersion >= 5)
            {
                return Path.Combine(engine.TargetPath, "Engine", "Binaries", "DotNET", "UnrealBuildTool", "UnrealBuildTool.exe");
            }

            return Path.Combine(engine.TargetPath, "Engine", "Binaries", "DotNET", "UnrealBuildTool.exe");
        }
    }
}