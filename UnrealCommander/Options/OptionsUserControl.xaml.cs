using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using UnrealAutomationCommon.Operations;
using UnrealCommander.Annotations;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for OptionsUserControl.xaml
    /// </summary>
    public partial class OptionsUserControl : UserControl, INotifyPropertyChanged
    {
        private OperationTarget _operationTarget = null;
        private Operation _operation = null;

        public OperationTarget OperationTarget
        {
            get => _operationTarget;
            set
            {
                _operationTarget = value;
                OnPropertyChanged();
            }
        }

        public Operation Operation
        {
            get => _operation;
            set
            {
                _operation = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
