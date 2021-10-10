﻿using System;
using System.IO;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Plugin : OperationTarget, IEngineInstallProvider
    {
        private PluginDescriptor _pluginDescriptor;
        private string _uPluginPath;
        private FileSystemWatcher _watcher;

        [JsonConstructor]
        public Plugin([JsonProperty("UPluginPath")] string path)
        {
            if (IsPluginFile(path))
                UPluginPath = path;
            else
                UPluginPath = FindUPlugin(path);
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
                        if (Args.FullPath == UPluginPath) LoadDescriptor();
                    };
                    _watcher.EnableRaisingEvents = true;
                }

                OnPropertyChanged();
                OnPropertyChanged(nameof(Name));
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
                    if (Path.GetPathRoot(projectPath) == projectPath) throw new Exception("No .uproject found in " + projectPath);

                    projectPath = Path.GetFullPath(Path.Combine(projectPath, @"..\")); // Up 1 level
                    uProjectFiles = Directory.GetFiles(projectPath, "*.uproject");
                }

                string uProjectPath = uProjectFiles[0];

                return uProjectPath;
            }
        }

        public EngineInstall EngineInstall => PluginDescriptor?.EngineInstall;

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
            if (EngineInstall == null) return false;

            return EngineInstall.SupportsConfiguration(configuration);
        }

        public static bool IsPluginFile(string path)
        {
            return FileUtils.HasExtension(path, ".uplugin");
        }

        public static string FindUPlugin(string path)
        {
            if (!Directory.Exists(path)) return null;

            string[] files = Directory.GetFiles(path);
            foreach (string file in files)
                if (Path.GetExtension(file).Equals(".uplugin", StringComparison.InvariantCultureIgnoreCase))
                    return file;

            return null;
        }
    }
}