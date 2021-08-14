using Newtonsoft.Json;
using System;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Plugin : OperationTarget, IEngineInstallProvider
    {
        private string _uPluginPath;

        [JsonConstructor]
        public Plugin(string uPluginPath)
        {
            UPluginPath = uPluginPath;
            LoadDescriptor();
        }

        public string UPluginPath
        {
            get => _uPluginPath;
            set
            {
                _uPluginPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Name));
            }
        }

        [JsonIgnore]
        public Project HostProject => new Project(HostProjectUProjectPath);

        public override string Name => Path.GetFileNameWithoutExtension(UPluginPath) ?? "Invalid";
        public override string TargetPath => UPluginPath;
        public EngineInstall EngineInstall => PluginDescriptor?.GetEngineInstall();

        [JsonIgnore]
        public PluginDescriptor PluginDescriptor { get; private set; }
        [JsonIgnore]
        public string HostProjectPath => Path.GetFullPath(Path.Combine(GetPluginPath(), @"..\..\")); // Up 2 levels

        [JsonIgnore]
        public string HostProjectUProjectPath{
            get
            {
                // Get project path
                string[] uProjectFiles;
                string projectPath = HostProjectPath;
                uProjectFiles = Directory.GetFiles(projectPath, "*.uproject");

                while (uProjectFiles.Length < 1)
                {
                    if (Path.GetPathRoot(projectPath) == projectPath)
                    {
                        throw new Exception("No .uproject found in " + projectPath);
                    }

                    projectPath = Path.GetFullPath(Path.Combine(projectPath, @"..\")); // Up 1 level
                    uProjectFiles = Directory.GetFiles(projectPath, "*.uproject");
                }

                string uProjectPath = uProjectFiles[0];

                return uProjectPath;
            }
        }

        public override void LoadDescriptor()
        {
            PluginDescriptor = PluginDescriptor.Load(UPluginPath);
        }

        public string GetPluginPath()
        {
            return Path.GetDirectoryName(_uPluginPath);
        }

        public override bool SupportsConfiguration(BuildConfiguration configuration)
        {
            return EngineInstall.SupportsConfiguration(configuration);
        }
    }
}
