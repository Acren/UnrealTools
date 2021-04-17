using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UnrealAutomationCommon.Operations
{
    public class OperationParameters : INotifyPropertyChanged
    {
        private Project _project;
        private Plugin _plugin;
        private BuildConfiguration _configuration;
        private bool _useInsights = false;
        private bool _stompMalloc = false;
        private bool _waitForAttach = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public OperationParameters()
        {
        }

        public Project Project
        {
            get => _project;
            set
            {
                if(_project != value)
                {
                    if(_project != null)
                    {
                        _project.PropertyChanged -= ProjectChanged;
                    }
                    _project = value;
                    if (_project != null)
                    {
                        _project.PropertyChanged += ProjectChanged;
                        Plugin = null;
                    }
                    OnPropertyChanged();
                }
                void ProjectChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                }
            }
        }

        public Plugin Plugin
        {
            get => _plugin;
            set
            {
                if (_plugin != value)
                {
                    if (_plugin != null)
                    {
                        _plugin.PropertyChanged -= PluginChanged;
                    }
                    _plugin = value;
                    if (_plugin != null)
                    {
                        _plugin.PropertyChanged += PluginChanged;
                        Project = null;
                    }
                    OnPropertyChanged();
                }
                void PluginChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                }
            }
        }

        public BuildConfiguration Configuration
        {
            get => _configuration;
            set
            {
                _configuration = value;
                OnPropertyChanged();
            }
        }

        public bool UseInsights
        {
            get => _useInsights;
            set
            {
                _useInsights = value;
                OnPropertyChanged();
            }
        }

        public bool StompMalloc
        {
            get => _stompMalloc;
            set
            {
                _stompMalloc = value;
                OnPropertyChanged();
            }
        }

        public bool WaitForAttach
        {
            get => _waitForAttach;
            set
            {
                _waitForAttach = value;
                OnPropertyChanged();
            }
        }

        public string OutputPathRoot => @"C:\UnrealCommander\";
        public bool UseOutputPathProjectSubfolder => true;
        public bool UseOutputPathOperationSubfolder => true;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
