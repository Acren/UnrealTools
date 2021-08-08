using Newtonsoft.Json;
using System;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Plugin : OperationTarget
    {
        private string _uPluginPath;

        // Default constructor is needed to support adding rows from DataGrid
        public Plugin()
        {
        }

        public Plugin(string Path)
        {
            UPluginPath = Path;
            if (PluginUtils.IsPluginFile(UPluginPath))
            {
                LoadDescriptor();
            }
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
        [JsonIgnore]
        public Project HostProject => new Project(HostProjectUProjectPath);

        public override string Name => Path.GetFileNameWithoutExtension(UPluginPath) ?? "Invalid";
        public override EngineInstall EngineInstall => PluginDescriptor?.GetEngineInstall();

        public override void LoadDescriptor()
        {
            PluginDescriptor = PluginDescriptor.Load(UPluginPath);
        }

        public string GetPluginPath()
        {
            return Path.GetDirectoryName(_uPluginPath);
        }

    }
}
