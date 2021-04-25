using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public class Project : OperationTarget
    {
        private string _uProjectPath;
        private string _testName;

        public Project()
        {
        }

        public Project(string Path)
        {
            UProjectPath = Path;
            if (ProjectUtils.IsProjectFile(UProjectPath))
            {
                LoadDescriptor();
            }
        }

        public string UProjectPath
        {
            get => _uProjectPath;
            set
            {
                _uProjectPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Name));
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
        public ProjectDescriptor ProjectDescriptor { get; private set; }

        public string Name => Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";

        public override void LoadDescriptor()
        {
            ProjectDescriptor = ProjectDescriptor.Load(UProjectPath);
        }

        public string GetProjectName()
        {
            return Path.GetFileNameWithoutExtension(_uProjectPath);
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

        public string GetStagedPackageExecutablePath()
        {
            return Path.Combine(GetStagedBuildWindowsPath(), GetProjectName() + ".exe");
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
