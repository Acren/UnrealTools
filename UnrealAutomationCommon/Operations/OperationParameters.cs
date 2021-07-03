using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public class OperationParameters : INotifyPropertyChanged
    {
        private OperationTarget _target;
        private BuildConfiguration _configuration;

        private BindingList<TraceChannel> _traceChannels;

        private bool _stompMalloc = false;
        private bool _waitForAttach = false;

        private bool _runTests = false;

        private string _additionalArguments;

        public event PropertyChangedEventHandler PropertyChanged;

        public OperationParameters()
        {
            TraceChannels = new BindingList<TraceChannel>();
            TraceChannels.RaiseListChangedEvents = true;
        }

        public BindingList<TraceChannel> TraceChannels
        {
            get => _traceChannels;
            private set
            {
                if (_traceChannels != null)
                {
                    _traceChannels.ListChanged -= CollectionChanged;
                }
                _traceChannels = value;
                if (_traceChannels != null)
                {
                    _traceChannels.ListChanged += CollectionChanged;
                }
                void CollectionChanged(object sender, ListChangedEventArgs args)
                {
                    OnPropertyChanged();
                }
            }

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

        public bool RunTests
        {
            get => _runTests;
            set
            {
                _runTests = value;
                OnPropertyChanged();
            }
        }

        public string AdditionalArguments
        {
            get => _additionalArguments;
            set
            {
                _additionalArguments = value;
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
