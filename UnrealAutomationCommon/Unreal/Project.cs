using System;
using System.Collections.Generic;
using System.IO;
using LocalAutomation.Core;
using LocalAutomation.Core.IO;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    [Target]
    public class Project : OperationTarget, IPackageProvider, IEngineInstanceProvider, IDisposable
    {
        private ProjectDescriptor _projectDescriptor = null!;
        private FileSystemWatcher? _watcher;
        private Exception? _backgroundException;

        [JsonConstructor]
        public Project(string targetPath)
        {
            if (!ProjectPaths.Instance.IsTargetDirectory(targetPath))
            {
                AppLogger.LoggerInstance.LogError($"Package {targetPath} does not contain a .uproject");
                return;
            }

            TargetPath = targetPath;

            LoadDescriptor();

            // Long-lived project targets keep their descriptor in sync with disk edits, but the watcher must never throw
            // because it runs on a background thread outside normal operation failure handling.
            InitializeWatcher();

            OnPropertyChanged();
            OnPropertyChanged(nameof(Name));
        }

        public string UProjectPath
        {
            get
            {
                ThrowIfBackgroundException();
                return ProjectPaths.Instance.FindRequiredTargetFile(TargetPath);
            }
        }

        public ProjectDescriptor ProjectDescriptor
        {
            get
            {
                ThrowIfBackgroundException();
                return _projectDescriptor;
            }
            private set
            {
                _projectDescriptor = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EngineInstance));
                OnPropertyChanged(nameof(EngineInstanceName));
            }
        }

        public Engine EngineInstance => ProjectDescriptor.Engine;

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
        public override string DisplayName => DirectoryName ?? Name;

        public override bool IsValid => ProjectPaths.Instance.IsTargetDirectory(TargetPath);

        public string ProjectPath => TargetPath;

        public string LogsPath => Path.Combine(ProjectPath, "Saved", "Logs");

        public string StagedBuildsPath => Path.Combine(ProjectPath, "Saved", "StagedBuilds");

        public string PluginsPath => Path.Combine(ProjectPath, "Plugins");

        public string SourcePath => Path.Combine(ProjectPath, "Source");

        /**
         * Plugins contained within the project directory
         * Does not include referenced plugins installed to the engine
         */
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

        public Package? GetProvidedPackage(Engine engineContext) => GetStagedPackage(engineContext);

        public override void LoadDescriptor()
        {
            FileUtils.WaitForFileReadable(UProjectPath);
            ProjectDescriptor = ProjectDescriptor.Load(UProjectPath);
        }

        /// <summary>
        /// Resolves the effective engine instance while recording the cost of descriptor-driven engine lookup for
        /// performance telemetry.
        /// </summary>
        public Engine GetEngineInstanceForDiagnostics()
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("Project.GetEngineInstance")
                .SetTag("descriptor.path", UProjectPath);
            Engine engine = EngineInstance;
            activity.SetTag("engine.name", engine.DisplayName);
            return engine;
        }

        public string GetStagedBuildWindowsPath(Engine engineContext)
        {
            return Path.Combine(StagedBuildsPath, engineContext.GetWindowsPlatformName());
        }

        /// <summary>
        /// Stops background watcher activity when the owner is finished with this target so later temp-directory churn
        /// cannot report stale file-system events against code that already moved on.
        /// </summary>
        public void Dispose()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        /// <summary>
        /// Starts the descriptor watcher for persistent project targets so editor-visible state tracks file changes.
        /// </summary>
        private void InitializeWatcher()
        {
            _watcher = new FileSystemWatcher(TargetPath);
            _watcher.Changed += HandleWatcherChanged;
            _watcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Reloads the project descriptor when the .uproject changes and records failures instead of letting the
        /// watcher thread terminate the whole process.
        /// </summary>
        private void HandleWatcherChanged(object sender, FileSystemEventArgs args)
        {
            try
            {
                string? projectPath = ProjectPaths.Instance.FindTargetFile(TargetPath);
                if (projectPath == null || !string.Equals(args.FullPath, projectPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                LoadDescriptor();
            }
            catch (Exception ex)
            {
                RecordBackgroundException(ex);
            }
        }

        /// <summary>
        /// Stores and logs watcher failures so background project reload issues stay visible and can be turned into a
        /// normal operation failure by the caller.
        /// </summary>
        private void RecordBackgroundException(Exception exception)
        {
            _backgroundException = exception;
            AppLogger.LoggerInstance.LogError(exception, "Project background watcher failed for '{ProjectPath}'.", TargetPath);
        }

        /// <summary>
        /// Converts a previously recorded watcher failure into a normal foreground exception so callers fail on their
        /// own thread instead of the process dying on the watcher thread.
        /// </summary>
        private void ThrowIfBackgroundException()
        {
            Exception? backgroundException = _backgroundException;
            if (backgroundException == null)
            {
                return;
            }

            _backgroundException = null;
            throw new InvalidOperationException($"Project target '{TargetPath}' encountered a background reload failure.", backgroundException);
        }

        public Package? GetStagedPackage(Engine engineContext)
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

        /**
         * Consider the project blueprint-only if it has zero modules
         * Alternatively it should also be possible to check the absence of a Source folder
         */
        public bool IsBlueprintOnly => ProjectDescriptor?.Modules.Count == 0;

        public void ConvertToBlueprintOnly()
        {
            // Remove source folder
            FileUtils.DeleteDirectoryIfExists(SourcePath);

            // Remove modules property
            JObject uProjectContents = JObject.Parse(File.ReadAllText(UProjectPath));
            uProjectContents.Remove("Modules");

            File.WriteAllText(UProjectPath, uProjectContents.ToString());
        }

        public void SetProjectVersion(string version, ILogger logger)
        {
            string defaultGameIniPath = Path.Combine(TargetDirectory, "Config", "DefaultGame.ini");
            UnrealConfig config = new(defaultGameIniPath);
            ConfigSection? projectSettings = config.GetSection("/Script/EngineSettings.GeneralProjectSettings");
            if (projectSettings != null)
            {
                projectSettings.SetValue("ProjectVersion", version);
                config.Save();
                logger.LogInformation($"Updated project version to {version}");
            }
            else
            {
                logger.LogWarning("Could not find GeneralProjectSettings section to update project version");
            }
        }

    }
}
