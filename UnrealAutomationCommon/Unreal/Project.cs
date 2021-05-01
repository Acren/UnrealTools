using System.IO;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public class Project : OperationTarget
    {
        private string _uProjectPath;
        private string _testName;
        private ProjectDescriptor _projectDescriptor;

        private FileSystemWatcher watcher;

        // Default constructor is needed to support adding rows from DataGrid
        public Project()
        {
        }

        public Project(string path)
        {
            UProjectPath = path;
        }

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
                    watcher = new FileSystemWatcher(Path.GetDirectoryName( _uProjectPath));
                    watcher.Changed += (Sender, Args) =>
                    {
                        if (Args.FullPath == UProjectPath)
                        {
                            LoadDescriptor();
                        }
                    };
                    watcher.EnableRaisingEvents = true;

                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        public string TestName
        {
            get => _testName;
            set
            {
                _testName = value;
                OnPropertyChanged();
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
            }
        }

        public string Name => Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";

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
            return Path.Combine(GetStagedBuildsPath(), "WindowsNoEditor");
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

        public override string GetName()
        {
            return Name;
        }

        public override EngineInstall GetEngineInstall()
        {
            return ProjectDescriptor.GetEngineInstall();
        }
    }
}
