using System;
using System.IO;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    [JsonObject(MemberSerialization.OptIn)]
    public class Engine : OperationTarget, IEngineInstanceProvider
    {
        [JsonProperty]
        public string Key { get; set; }

        // Path of the engine instance location. Output path does not include the /Engine sub-directory.
        [JsonProperty]
        public override string TargetPath { get; }

        public bool IsSourceBuild { get; private set; }

        public override string Name => $"{Version.ToString()} {EngineType}";
        public override string DisplayName => Name ;

        public string EngineType => IsSourceBuild ? "Custom" : "Launcher";

        public string BaseEditorName => Version.MajorVersion >= 5 ? "UnrealEditor" : "UE4Editor";

        public EngineVersion Version => EngineVersion.Load(this.GetBuildVersionPath());

        public string PluginsPath => Path.Combine(TargetPath, "Engine", "Plugins");

        [JsonConstructor]
        public Engine(string targetPath)
        {
            TargetPath = targetPath;
            IsSourceBuild = File.Exists(Path.Combine(TargetPath, "Default.uprojectdirs"));
        }

        public override bool SupportsConfiguration(BuildConfiguration configuration)
        {
            if (configuration == BuildConfiguration.Debug
                || configuration == BuildConfiguration.Test)
            // Only support Debug and Test in source builds
            {
                return IsSourceBuild;
            }

            // Always support DebugGame, Development, Shipping
            return true;
        }

        public override void LoadDescriptor()
        {
            throw new NotImplementedException();
        }

        public bool SupportsTestReports => Version >= new EngineVersion(4, 25);

        public string GetWindowsPlatformName()
        {
            if (Version.MajorVersion >= 5)
            {
                return "Windows";
            }

            return "WindowsNoEditor";
        }

        public Plugin FindInstalledPlugin(string pluginName)
        {
            string plugins = Path.Combine(TargetPath, "Engine", "Plugins");
            string extension = "*.uplugin";
            string[] upluginPaths = Directory.GetFiles(plugins, extension, SearchOption.AllDirectories);
            foreach (string upluginPath in upluginPaths)
            {
                if (Path.GetFileNameWithoutExtension(upluginPath).Equals(pluginName, StringComparison.InvariantCultureIgnoreCase))
                {
                    return new Plugin(upluginPath);
                }
            }

            return null;
        }

        public Engine EngineInstance => this;
    }
}