using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealCommander.Annotations;

namespace UnrealCommander.Options
{
    /// <summary>
    ///     Interaction logic for OptionsUserControl.xaml
    /// </summary>
    public partial class OptionsUserControl /*<T>*/ : UserControl, INotifyPropertyChanged /*where T : OperationOptions*/
    {
        private Operation _operation;
        private OperationTarget _operationTarget;

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

        public OperationOptions Options => DataContext as OperationOptions;

        public event PropertyChangedEventHandler PropertyChanged;

        public T GetOptions<T>() where T : OperationOptions
        {
            return Options as T;
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

        //protected virtual void OptionsChanged()
        //{
        //}

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}