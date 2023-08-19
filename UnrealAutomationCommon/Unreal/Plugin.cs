using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Plugin : OperationTarget, IEngineInstanceProvider
    {
        private PluginDescriptor _pluginDescriptor;

        private FileSystemWatcher _watcher;

        [JsonConstructor]
        public Plugin(string targetPath)
        {
            if (!PluginPaths.Instance.IsTargetDirectory(targetPath))
            {
                throw new ArgumentException("Does not contain a .uplugin",  nameof(targetPath));
            }

            TargetPath = targetPath;

            LoadDescriptor();

            // Reload descriptor if it changes
            _watcher = new FileSystemWatcher(TargetPath);
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

        public string UPluginPath => PluginPaths.Instance.FindTargetFile(TargetPath);

        public Project HostProject => new(HostProjectPath);

        public override IOperationTarget ParentTarget => HostProject;

        public override string Name => DirectoryName;

        public override bool IsValid => PluginPaths.Instance.IsTargetDirectory(TargetPath);

        public PluginDescriptor PluginDescriptor
        {
            get => _pluginDescriptor;
            private set
            {
                _pluginDescriptor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EngineInstance));
                OnPropertyChanged(nameof(EngineInstanceName));
            }
        }

        public string PluginPath => TargetPath;

        public string HostProjectPath => Path.GetFullPath(Path.Combine(PluginPath, @"..\..\")); // Up 2 levels

        public Engine EngineInstance
        {
            get
            {
                // If plugin descriptor has an engine version, find engine install using that
                EngineVersion descriptorVersion = PluginDescriptor?.EngineVersion;
                if (descriptorVersion != null)
                {
                    return EngineFinder.GetEngineInstall(descriptorVersion);
                }

                // Use host project version
                if (HostProject != null)
                {
                    return HostProject.EngineInstance;
                }

                // No descriptor version and no host project, fall back to default
                return EngineFinder.GetDefaultEngineInstall();
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

        public override void LoadDescriptor()
        {
            PluginDescriptor = PluginDescriptor.Load(UPluginPath);
        }

        public override bool SupportsConfiguration(BuildConfiguration configuration)
        {
            if (EngineInstance == null)
            {
                return false;
            }

            return EngineInstance.SupportsConfiguration(configuration);
        }

    }
}