using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Semver;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Plugin : OperationTarget, IEngineInstanceProvider, IDisposable
    {
        private PluginDescriptor _pluginDescriptor = null!;
        private Project? _hostProject;

        private FileSystemWatcher? _watcher;
        private Exception? _backgroundException;

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

            // Long-lived plugin targets keep their descriptor in sync with disk edits, but the watcher must never throw
            // because it runs on a background thread outside normal operation failure handling.
            InitializeWatcher();
        }

        public string UPluginPath
        {
            get
            {
                ThrowIfBackgroundException();
                return PluginPaths.Instance.FindRequiredTargetFile(TargetPath);
            }
        }

        /// <summary>
        /// Reuses the resolved host project so repeated validation and command-preview reads do not recreate the same
        /// watcher-backed project object over and over.
        /// </summary>
        public Project HostProject => ResolveHostProject();

        public override IOperationTarget ParentTarget => HostProject;

        public override string Name => DirectoryName ?? "Invalid";

        public override bool IsValid => PluginPaths.Instance.IsTargetDirectory(TargetPath) && PluginDescriptor != null;

        public PluginDescriptor PluginDescriptor
        {
            get
            {
                ThrowIfBackgroundException();
                return _pluginDescriptor;
            }
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
        public bool IsBlueprintOnly => PluginDescriptor?.Modules?.Count == 0;
        
        /**
         * Check if the plugin has runtime modules (modules that will be included in packaged builds)
         * Runtime modules are those that are not editor-only types
         */
        public bool HasRuntimeModules => PluginDescriptor?.Modules?.Any(m => m.Type != "Editor" && 
                                                                             m.Type != "EditorNoCommandlet" && 
                                                                             m.Type != "EditorAndProgram" &&
                                                                             m.Type != "UncookedOnly") == true;

        public string PluginPath => TargetPath;

        public string HostProjectPath => string.IsNullOrEmpty(PluginPath) ? "" : Path.GetFullPath(Path.Combine(PluginPath, @"..\..\")); // Up 2 levels

        public Engine EngineInstance
        {
            get
            {
                // If plugin descriptor has an engine version, find engine install using that
                EngineVersion? descriptorVersion = PluginDescriptor?.EngineVersion;
                if (descriptorVersion != null)
                {
                    return EngineFinder.GetRequiredEngineInstall(descriptorVersion);
                }

                // Use host project version
                return HostProject.EngineInstance;
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

        /// <summary>
        /// Creates the host project while recording the cost of that resolution for performance telemetry.
        /// </summary>
        public Project GetHostProjectForDiagnostics()
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("Plugin.GetHostProject")
                .SetTag("host_project.path", HostProjectPath);
            bool cacheHit = _hostProject != null;
            Project hostProject = ResolveHostProject();
            activity.SetTag("cache.hit", cacheHit)
                .SetTag("target.type", hostProject.GetType().Name)
                .SetTag("is_valid", hostProject.IsValid);
            return hostProject;
        }

        /// <summary>
        /// Lazily creates the host project once so repeated property reads reuse the same descriptor and file watcher.
        /// </summary>
        private Project ResolveHostProject()
        {
            _hostProject ??= new Project(HostProjectPath);
            return _hostProject;
        }

        /// <summary>
        /// Stops background watcher activity when the owner is finished with this target so later temp-directory churn
        /// cannot report stale file-system events against an operation that already ended.
        /// </summary>
        public void Dispose()
        {
            _watcher?.Dispose();
            _watcher = null;

            if (_hostProject is IDisposable disposableHostProject)
            {
                disposableHostProject.Dispose();
            }

            _hostProject = null;
        }

        /// <summary>
        /// Starts the descriptor watcher for persistent plugin targets so UI-bound state stays in sync with disk edits.
        /// </summary>
        private void InitializeWatcher()
        {
            _watcher = new FileSystemWatcher(TargetPath);
            _watcher.Changed += HandleWatcherChanged;
            _watcher.EnableRaisingEvents = true;
        }

        /// <summary>
        /// Reloads the descriptor only when the plugin file itself changed and records any failure so background watcher
        /// errors can be surfaced to the active operation instead of crashing the process.
        /// </summary>
        private void HandleWatcherChanged(object sender, FileSystemEventArgs args)
        {
            try
            {
                string? pluginPath = PluginPaths.Instance.FindTargetFile(TargetPath);
                if (pluginPath == null || !string.Equals(args.FullPath, pluginPath, StringComparison.OrdinalIgnoreCase))
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
        /// Stores the latest watcher failure and logs it immediately so background file-system issues remain visible even
        /// before an operation turns them into a normal failure result.
        /// </summary>
        private void RecordBackgroundException(Exception exception)
        {
            _backgroundException = exception;
            AppLogger.LoggerInstance.LogError(exception, "Plugin background watcher failed for '{PluginPath}'.", TargetPath);
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
            throw new InvalidOperationException($"Plugin target '{TargetPath}' encountered a background reload failure.", backgroundException);
        }

        public bool UpdateVersionInteger()
        {
            SemVersion version = PluginDescriptor.SemVersion;
            int versionInt = version.ToInt();
            JObject descriptorJObject = JObject.Parse(File.ReadAllText(UPluginPath));
            string versionKey = "Version";
            JToken? versionToken = descriptorJObject[versionKey];
            if (versionToken != null && versionToken.ToObject<int>() == versionInt)
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
