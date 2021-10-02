using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Project : OperationTarget, IPackageProvider, IEngineInstallProvider
    {
        private string _uProjectPath;
        private ProjectDescriptor _projectDescriptor;
        private FileSystemWatcher _watcher;

        [JsonConstructor]
        public Project(string uProjectPath)
        {
            if (IsProjectFile(uProjectPath))
            {
                UProjectPath = uProjectPath;
            }
        }

        [JsonIgnore]
        public override string Name => Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";

        [JsonIgnore]
        public override string TargetPath => UProjectPath;

        [JsonIgnore]
        public EngineInstall EngineInstall => ProjectDescriptor?.EngineInstall;

        [JsonIgnore]
        public string EngineInstallName => EngineInstall != null ? EngineInstall.DisplayName : ProjectDescriptor?.EngineAssociation;

        [JsonIgnore]
        public Package ProvidedPackage => GetStagedPackage();

        public string UProjectPath
        {
            get => _uProjectPath;
            set
            {
                if (_uProjectPath != value)
                {
                    _uProjectPath = value;
                    LoadDescriptor();

                    // Reload descriptor if it changes
                    _watcher = new FileSystemWatcher(Path.GetDirectoryName(_uProjectPath));
                    _watcher.Changed += (Sender, Args) =>
                    {
                        if (Args.FullPath == UProjectPath)
                        {
                            LoadDescriptor();
                        }
                    };
                    _watcher.EnableRaisingEvents = true;

                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        [JsonIgnore]
        public ProjectDescriptor ProjectDescriptor
        {
            get => _projectDescriptor;
            private set
            {
                _projectDescriptor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EngineInstall));
                OnPropertyChanged(nameof(EngineInstallName));
            }
        }

        public override void LoadDescriptor()
        {
            FileUtils.WaitForFileReadable(UProjectPath);
            ProjectDescriptor = ProjectDescriptor.Load(UProjectPath);
        }

        public string GetProjectPath()
        {
            return Path.GetDirectoryName(_uProjectPath);
        }

        public string GetStagedBuildsPath()
        {
            return Path.Combine(GetProjectPath(), "Saved", "StagedBuilds");
        }

        public string GetStagedBuildWindowsPath()
        {
            return Path.Combine(GetStagedBuildsPath(), EngineInstall.GetWindowsPlatformName());
        }

        public Package GetStagedPackage()
        {
            string path = GetStagedBuildWindowsPath();
            return Package.IsPackageDirectory(path) ? new Package(path) : null;
        }

        public string GetStagedPackageExecutablePath()
        {
            return Path.Combine(GetStagedBuildWindowsPath(), Name + ".exe");
        }

        public string GetLogsPath()
        {
            return Path.Combine(GetProjectPath(), "Saved", "Logs");
        }

        public override bool SupportsConfiguration(BuildConfiguration configuration)
        {
            if (EngineInstall == null)
            {
                return false;
            }

            return EngineInstall.SupportsConfiguration(configuration);
        }

        public List<Plugin> GetPlugins()
        {
            List<Plugin> plugins = new List<Plugin>();
            foreach (string pluginPath in Directory.GetDirectories(Path.Combine(GetProjectPath(), "Plugins")))
            {
                Plugin plugin = new Plugin(pluginPath);
                plugins.Add(plugin);
            }

            return plugins;
        }

        public static bool IsProjectFile(string path)
        {
            return FileUtils.HasExtension(path, ".uproject");
        }
    }
}
