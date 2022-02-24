using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Plugin : OperationTarget, IEngineInstallProvider
    {
        private PluginDescriptor _pluginDescriptor;
        private string _uPluginPath;
        private List<EngineInstallVersion> _targetEngineVersions;

        private FileSystemWatcher _watcher;

        [JsonConstructor]
        public Plugin([JsonProperty("UPluginPath")] string path)
        {
            if (PluginPaths.Instance.IsTargetFile(path))
            {
                UPluginPath = path;
            }
            else
            {
                UPluginPath = PluginPaths.Instance.FindTargetFile(path);
            }
        }

        [JsonProperty]
        public string UPluginPath
        {
            get => _uPluginPath;
            set
            {
                _uPluginPath = value;
                if (_uPluginPath != null)
                {
                    LoadDescriptor();

                    // Reload descriptor if it changes
                    _watcher = new FileSystemWatcher(Path.GetDirectoryName(_uPluginPath));
                    _watcher.Changed += (Sender, Args) =>
                    {
                        if (Args.FullPath == UPluginPath)
                        {
                            try
                            {
                                LoadDescriptor();
                            }
                            catch (Exception)
                            {
                                // Ignore on exception, old descriptor will be preserved
                            }
                        }
                    };
                    _watcher.EnableRaisingEvents = true;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(Name));
            }
        }

        [JsonProperty]
        public List<EngineInstallVersion> TargetEngineVersions
        {
            get => _targetEngineVersions;
            set
            {
                _targetEngineVersions = value;
                OnPropertyChanged();
            }
        }

        public Project HostProject => new(HostProjectUProjectPath);

        public override IOperationTarget ParentTarget => HostProject;

        public override string Name => DirectoryName; // Path.GetFileNameWithoutExtension(UPluginPath) ?? "Invalid";
        public override string TargetPath => UPluginPath;

        public PluginDescriptor PluginDescriptor
        {
            get => _pluginDescriptor;
            private set
            {
                _pluginDescriptor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EngineInstall));
                OnPropertyChanged(nameof(EngineInstallName));
            }
        }

        public string HostProjectPath => Path.GetFullPath(Path.Combine(GetPluginPath(), @"..\..\")); // Up 2 levels

        public string HostProjectUProjectPath
        {
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

        public EngineInstall EngineInstall
        {
            get
            {
                // If plugin descriptor has an engine version, find engine install using that
                EngineInstallVersion descriptorVersion = PluginDescriptor?.EngineInstallVersion;
                if (descriptorVersion != null)
                {
                    return EngineInstallFinder.GetEngineInstall(descriptorVersion);
                }

                // Use host project version
                if (HostProject != null)
                {
                    return HostProject.EngineInstall;
                }

                // No descriptor version and no host project, fall back to default
                return EngineInstallFinder.GetDefaultEngineInstall();
            }
        }

        public string EngineInstallName => EngineInstall != null ? EngineInstall.DisplayName : PluginDescriptor?.EngineVersion;

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
            if (EngineInstall == null)
            {
                return false;
            }

            return EngineInstall.SupportsConfiguration(configuration);
        }

    }
}