using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace UnrealAutomationCommon
{
    public class Project : INotifyPropertyChanged
    {
        private string _uProjectPath;
        public event PropertyChangedEventHandler PropertyChanged;

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

        [JsonIgnore]
        public ProjectDescriptor ProjectDescriptor { get; private set; }

        public string Name => Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void LoadDescriptor()
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
    }
}
