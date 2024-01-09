using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Project : OperationTarget, IPackageProvider, IEngineInstanceProvider
    {
        private ProjectDescriptor _projectDescriptor;
        private FileSystemWatcher _watcher;

        [JsonConstructor]
        public Project(string targetPath)
        {
            if (!ProjectPaths.Instance.IsTargetDirectory(targetPath))
            {
                AppLogger.Instance.Log($"Package {targetPath} does not contain a .uproject", LogVerbosity.Error);
                return;
            }

            TargetPath = targetPath;

            LoadDescriptor();

            // Reload descriptor if it changes
            _watcher = new FileSystemWatcher(TargetPath);
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

        public string UProjectPath => ProjectPaths.Instance.FindTargetFile(TargetPath);

        public ProjectDescriptor ProjectDescriptor
        {
            get => _projectDescriptor;
            private set
            {
                _projectDescriptor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EngineInstance));
                OnPropertyChanged(nameof(EngineInstanceName));
            }
        }

        public Engine EngineInstance
        {
            get
            {
                if (ProjectDescriptor is { Engine: { } })
                {
                    return ProjectDescriptor.Engine;
                }

                return null;
            }
        }

        public string EngineInstanceName
        {
            get
            {
                if (EngineInstance != null)
                {
                    return EngineInstance.DisplayName;
                }

                return "None";
            }
        }

        public override string Name => Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";
        public override string DisplayName => DirectoryName;

        public override bool IsValid => ProjectPaths.Instance.IsTargetDirectory(TargetPath);

        public string ProjectPath => TargetPath;

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

        public Package GetProvidedPackage(Engine engineContext) => GetStagedPackage(engineContext);

        public override bool SupportsConfiguration(BuildConfiguration configuration)
        {
            if (EngineInstance == null)
            {
                return false;
            }

            return EngineInstance.SupportsConfiguration(configuration);
        }

        public override void LoadDescriptor()
        {
            FileUtils.WaitForFileReadable(UProjectPath);
            ProjectDescriptor = ProjectDescriptor.Load(UProjectPath);
        }

        public string GetStagedBuildWindowsPath(Engine engineContext)
        {
            return Path.Combine(StagedBuildsPath, (engineContext ?? EngineInstance).GetWindowsPlatformName());
        }

        public Package GetStagedPackage(Engine engineContext)
        {
            string path = GetStagedBuildWindowsPath(engineContext);
            return PackagePaths.Instance.IsTargetDirectory(path) ? new Package(path) : null;
        }

        public string GetStagedPackageExecutablePath(Engine engineContext)
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