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
        public bool IsSourceBuild = false;

        [JsonProperty]
        public string Key { get; set; }

        // Path of the engine instance location. Output path does not include the /Engine sub-directory.
        [JsonProperty]
        public string InstallDirectory { get; set; }

        public override string Name => DisplayName;
        public override string DisplayName => Version.ToString();
        public override string TargetPath => InstallDirectory;
        public string LocationString => IsSourceBuild ? InstallDirectory : "Launcher";

        public string BaseEditorName => Version.MajorVersion >= 5 ? "UnrealEditor" : "UE4Editor";

        public EngineVersion Version => EngineVersion.Load(this.GetBuildVersionPath());

        public string PluginsPath => Path.Combine(InstallDirectory, "Engine", "Plugins");

        [JsonConstructor]
        public Engine(string enginePath)
        {
            InstallDirectory = enginePath;
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
            string plugins = Path.Combine(InstallDirectory, "Engine", "Plugins");
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
        public string EngineInstanceName { get; }
    }
}