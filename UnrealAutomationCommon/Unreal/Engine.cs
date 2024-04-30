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

        public bool IsSourceBuild { get; private set; }

        public override string Name => $"{Version?.ToString() ?? "Invalid"} {EngineType}";
        public override string DisplayName => Name ;

        public string EngineType => IsSourceBuild ? "Custom" : "Launcher";

        public string BaseEditorName => Version?.MajorVersion >= 5 ? "UnrealEditor" : "UE4Editor";

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
                    return new Plugin(Path.GetDirectoryName(upluginPath));
                }
            }

            return null;
        }

        public bool IsPluginInstalled(string pluginName)
        {
            return FindInstalledPlugin(pluginName) != null;
        }

        public void UninstallPlugin(string pluginName)
        {
            Plugin targetPlugin = FindInstalledPlugin(pluginName);
            if (targetPlugin == null)
            {
                throw new Exception("Could not find plugin in installed plugins");
            }
            // For now, just delete the plugin files
            // If the plugin was installed via Epic Launcher, the plugin may remain registered there, might be something to improve
            targetPlugin.DeletePlugin();
        }

        public Engine EngineInstance => this;
    }
}