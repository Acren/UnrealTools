using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealCommander.Annotations;

namespace UnrealCommander.Options
{
    /// <summary>
    /// Interaction logic for OptionsUserControl.xaml
    /// </summary>
    public partial class OptionsUserControl/*<T>*/ : UserControl, INotifyPropertyChanged /*where T : OperationOptions*/
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

        //private T _options = null;

        //public T Options
        //{
        //    get => _options;
        //    set
        //    {
        //        _options = value;
        //        OptionsChanged();
        //        OnPropertyChanged();
        //    }
        //}

        protected virtual void OptionsChanged()
        {

        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
