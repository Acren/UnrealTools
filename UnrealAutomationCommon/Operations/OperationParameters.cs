using Newtonsoft.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UnrealAutomationCommon.Operations
{
    public class OperationParameters : INotifyPropertyChanged
    {
        private OperationTarget _target;
        private BuildConfiguration _configuration;

        private bool _traceCpu = false;
        private bool _traceFrame = false;
        private bool _traceBookmark = false;
        private bool _traceLoadTime = false;

        private bool _stompMalloc = false;
        private bool _waitForAttach = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public OperationParameters()
        {
        }

        public OperationTarget Target
        {
            get => _target;
            set
            {
                if (_target != value)
                {
                    if (_target != null)
                    {
                        _target.PropertyChanged -= TargetChanged;
                    }
                    _target = value;
                    if (_target != null)
                    {
                        _target.PropertyChanged += TargetChanged;
                    }
                    OnPropertyChanged();
                }
                void TargetChanged(object sender, PropertyChangedEventArgs args)
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

        public bool TraceCpu
        {
            get => _traceCpu;
            set
            {
                _traceCpu = value;
                OnPropertyChanged();
            }
        }

        public bool TraceFrame
        {
            get => _traceFrame;
            set
            {
                _traceFrame = value;
                OnPropertyChanged();
            }
        }

        public bool TraceBookmark
        {
            get => _traceBookmark;
            set
            {
                _traceBookmark = value;
                OnPropertyChanged();
            }
        }

        public bool TraceLoadTime
        {
            get => _traceLoadTime;
            set
            {
                _traceLoadTime = value;
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

        [JsonIgnore]
        public string OutputPathRoot => @"C:\UnrealCommander\";
        [JsonIgnore]
        public bool UseOutputPathProjectSubfolder => true;
        [JsonIgnore]
        public bool UseOutputPathOperationSubfolder => true;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
