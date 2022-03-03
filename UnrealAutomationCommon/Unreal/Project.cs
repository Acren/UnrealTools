using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Project : OperationTarget, IPackageProvider, IEngineInstallProvider
    {
        private ProjectDescriptor _projectDescriptor;
        private string _uProjectPath;
        private FileSystemWatcher _watcher;

        [JsonConstructor]
        public Project([JsonProperty("UProjectPath")] string path)
        {
            if (ProjectPaths.Instance.IsTargetFile(path))
            {
                UProjectPath = path;
            }
            else
            {
                UProjectPath = ProjectPaths.Instance.FindTargetFile(path);
            }
        }

        [JsonProperty]
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

        public EngineInstall EngineInstall => ProjectDescriptor?.EngineInstall;

        public string EngineInstallName => EngineInstall != null ? EngineInstall.DisplayName : ProjectDescriptor?.EngineAssociation;

        public override string Name => Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";
        public override string DisplayName => DirectoryName;

        public override string TargetPath => UProjectPath;

        public Package GetProvidedPackage(EngineInstall engineContext) => GetStagedPackage(engineContext);

        public override bool SupportsConfiguration(BuildConfiguration configuration)
        {
            if (EngineInstall == null)
            {
                return false;
            }

            return EngineInstall.SupportsConfiguration(configuration);
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

        public string GetStagedBuildWindowsPath(EngineInstall engineContext)
        {
            return Path.Combine(GetStagedBuildsPath(), (engineContext ?? EngineInstall).GetWindowsPlatformName());
        }

        public Package GetStagedPackage(EngineInstall engineContext)
        {
            string path = GetStagedBuildWindowsPath(engineContext);
            return PackagePaths.Instance.IsTargetDirectory(path) ? new Package(path) : null;
        }

        public string GetStagedPackageExecutablePath(EngineInstall engineContext)
        {
            return Path.Combine(GetStagedBuildWindowsPath(engineContext), Name + ".exe");
        }

        public string GetLogsPath()
        {
            return Path.Combine(GetProjectPath(), "Saved", "Logs");
        }

        public List<Plugin> GetPlugins()
        {
            var plugins = new List<Plugin>();
            foreach (string pluginPath in Directory.GetDirectories(Path.Combine(GetProjectPath(), "Plugins")))
            {
                Plugin plugin = new(pluginPath);
                plugins.Add(plugin);
            }

            return plugins;
        }

    }
}