using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealCommander
{
    public class TraceChannelOption : INotifyPropertyChanged
    {
        private TraceChannel _traceChannel = null;
        private bool _enabled = false;

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
                _enabled = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
