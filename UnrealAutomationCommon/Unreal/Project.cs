using System.IO;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Project : OperationTarget, IPackageProvider, IEngineInstallProvider
    {
        private string _uProjectPath;
        private ProjectDescriptor _projectDescriptor;
        private FileSystemWatcher _watcher;

        // Default constructor is needed to support adding rows from DataGrid
        public Project()
        {
        }

        public Project(string uProjectPath)
        {
            UProjectPath = uProjectPath;
        }

        [JsonIgnore]
        public override string Name => Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";

        [JsonIgnore]
        public override string TargetPath => UProjectPath;

        [JsonIgnore]
        public EngineInstall EngineInstall => ProjectDescriptor.EngineInstall;

        [JsonIgnore]
        public string EngineInstallName => EngineInstall != null ? EngineInstall.DisplayName : ProjectDescriptor.EngineAssociation;

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
                    _watcher = new FileSystemWatcher(Path.GetDirectoryName( _uProjectPath));
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
            return Package.IsPackage(path) ? new Package(path) : null;
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
    }
}
