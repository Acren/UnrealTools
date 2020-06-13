using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon;

namespace UnrealAutomationCommon
{
    public class Project : INotifyPropertyChanged
    { 
        private string uProjectPath;
        public event PropertyChangedEventHandler PropertyChanged;

        public Project()
        {
        }

        public Project(string Path)
        {
            UProjectPath = Path;
            if(ProjectDefinition.IsProjectFile(UProjectPath))
            {
                Initialize();
            }
        }

        public string UProjectPath
        {
            get { return uProjectPath; }
            set
            {
                uProjectPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Name));
            }
        }

        public ProjectDefinition ProjectDefinition { get; set; }

        public string Name
        {
            get
            {
                return Path.GetFileNameWithoutExtension(UProjectPath) ?? "Invalid";
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Initialize()
        {
            ProjectDefinition = ProjectDefinition.Load(UProjectPath);
        }
    }
}
