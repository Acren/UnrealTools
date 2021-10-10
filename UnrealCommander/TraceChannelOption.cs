using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander
{
    public class TraceChannelOption : INotifyPropertyChanged
    {
        private bool _enabled;
        private TraceChannel _traceChannel;

        public TraceChannel TraceChannel
        {
            get => _traceChannel;
            set
            {
                _traceChannel = value;
                OnPropertyChanged();
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}