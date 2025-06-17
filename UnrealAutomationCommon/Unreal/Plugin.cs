using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Semver;
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
                AppLogger.LoggerInstance.LogError($"Package {targetPath} does not contain a .uplugin");
                return;
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

        /**
         * Consider the plugin blueprint-only if it has zero modules
         * Alternatively it should also be possible to check the absence of a Source folder
         */
        public bool IsBlueprintOnly => PluginDescriptor.Modules.Count == 0;
        
        /**
         * Check if the plugin has runtime modules (modules that will be included in packaged builds)
         * Runtime modules are those that are not editor-only types
         */
        public bool HasRuntimeModules => PluginDescriptor.Modules.Any(m => m.Type != "Editor" && 
                                                                           m.Type != "EditorNoCommandlet" && 
                                                                           m.Type != "EditorAndProgram" &&
                                                                           m.Type != "UncookedOnly");

        public string PluginPath => TargetPath;

        public string HostProjectPath => string.IsNullOrEmpty(PluginPath) ? "" : Path.GetFullPath(Path.Combine(PluginPath, @"..\..\")); // Up 2 levels

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

        public bool UpdateVersionInteger()
        {
            SemVersion version = PluginDescriptor.SemVersion;
            int versionInt = version.ToInt();
            JObject descriptorJObject = JObject.Parse(File.ReadAllText(UPluginPath));
            string versionKey = "Version";
            if (descriptorJObject[versionKey].ToObject<int>() == versionInt)
            {
                // Already correct, did not update
                return false;
            }
            descriptorJObject[versionKey] = versionInt;

            using FileStream fs = File.Create(UPluginPath);
            using StreamWriter sw = new(fs);
            using JsonTextWriter jtw = new(sw)
            {
                Formatting = Formatting.Indented,
                Indentation = 1,
                IndentChar = '\t'
            };
            (new JsonSerializer()).Serialize(jtw, descriptorJObject);

            return true;
        }

        public void DeletePlugin()
        {
            FileUtils.DeleteDirectory(PluginPath);
        }

    }
}