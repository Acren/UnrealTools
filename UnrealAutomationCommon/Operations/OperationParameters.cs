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
                    }
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
                    }
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

        public string OutputPathRoot { get; set; }
        public bool UseOutputPathProjectSubfolder { get; set; }
        public bool UseOutputPathOperationSubfolder { get; set; }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
