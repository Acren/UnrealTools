using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
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

        public EngineInstall EngineInstall
        {
            get
            {
                if (ProjectDescriptor is { EngineInstall: { } })
                {
                    return ProjectDescriptor.EngineInstall;
                }

                return null;
            }
        }

        public string EngineInstallName
        {
            get
            {
                if (EngineInstall != null)
                {
                    return EngineInstall.DisplayName;
                }

                return "None";
            }
        }

        public override string Name => Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";
        public override string DisplayName => DirectoryName;

        public override string TargetPath => UProjectPath;

        public override bool IsValid => ProjectPaths.Instance.IsTargetFile(TargetPath);

        public string ProjectPath => Path.GetDirectoryName(_uProjectPath);

        public string LogsPath => Path.Combine(ProjectPath, "Saved", "Logs");

        public string StagedBuildsPath => Path.Combine(ProjectPath, "Saved", "StagedBuilds");

        public string PluginsPath => Path.Combine(ProjectPath, "Plugins");

        public string SourcePath => Path.Combine(ProjectPath, "Source");

        public List<Plugin> Plugins
        {
            get
            {
                var plugins = new List<Plugin>();
                foreach (string pluginPath in Directory.GetDirectories(Path.Combine(ProjectPath, "Plugins")))
                {
                    // Check it's a valid plugin directory, there might be empty directories lying around
                    if (PluginPaths.Instance.IsTargetDirectory(pluginPath))
                    {
                        Plugin plugin = new(pluginPath);
                        plugins.Add(plugin);
                    }
                }

                return plugins;
            }
        }

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

        public string GetStagedBuildWindowsPath(EngineInstall engineContext)
        {
            return Path.Combine(StagedBuildsPath, (engineContext ?? EngineInstall).GetWindowsPlatformName());
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

        // Copy the plugin into this project
        public void AddPlugin(string pluginPath)
        {
            FileUtils.CopyDirectory(pluginPath, PluginsPath, true);
        }

        // Copy the plugin into this project
        public void AddPlugin(Plugin plugin)
        {
            AddPlugin(plugin.PluginPath);
        }

        public void RemovePlugin(string pluginName)
        {
            foreach (Plugin plugin in Plugins)
            {
                if (plugin.Name == pluginName)
                {
                    FileUtils.DeleteDirectory(plugin.PluginPath);
                }
            }
        }

        public void ConvertToBlueprintOnly()
        {
            // Remove source folder
            FileUtils.DeleteDirectoryIfExists(SourcePath);

            // Remove modules property
            JObject uProjectContents = JObject.Parse(File.ReadAllText(UProjectPath));
            uProjectContents.Remove("Modules");

            File.WriteAllText(UProjectPath, uProjectContents.ToString());
        }

    }
}