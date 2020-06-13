using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UnrealAutomationCommon
{
    public class OperationParameters : INotifyPropertyChanged
    {
        private Project project;
        private BuildConfiguration configuration;
        private bool useInsights = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public OperationParameters()
        {
        }

        public Project Project
        {
            get { return project; }
            set
            {
                if(project != value)
                {
                    if(project != null)
                    {
                        project.PropertyChanged -= ProjectChanged;
                    }
                    project = value;
                    if (project != null)
                    {
                        project.PropertyChanged += ProjectChanged;
                    }
                }
                void ProjectChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                }
            }
        }

        public BuildConfiguration Configuration
        {
            get { return configuration; }
            set
            {
                configuration = value;
                OnPropertyChanged();
            }
        }

        public bool UseInsights
        {
            get
            {
                return useInsights;
            }
            set
            {
                useInsights = value;
                OnPropertyChanged();
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
