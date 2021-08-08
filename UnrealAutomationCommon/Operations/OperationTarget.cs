using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public interface IOperationTarget : INotifyPropertyChanged
    {
        public string Name { get;}
        public EngineInstall EngineInstall { get; }
        public string TestName { get; set; }
    }

    public abstract class OperationTarget : IOperationTarget
    {
        public abstract string Name { get; }
        public abstract EngineInstall EngineInstall { get; }

        private string _testName = string.Empty;

        public string TestName
        {
            get => _testName;
            set
            {
                _testName = value;
                OnPropertyChanged();
            }
        }

        public abstract void LoadDescriptor();

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

    }
}
