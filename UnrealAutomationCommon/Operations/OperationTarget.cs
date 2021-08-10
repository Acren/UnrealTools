using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public interface IOperationTarget : INotifyPropertyChanged
    {
        public string Name { get;}
        public string TestName { get; set; }

        public bool SupportsConfiguration(BuildConfiguration configuration);
    }

    public abstract class OperationTarget : IOperationTarget
    {
        public abstract string Name { get; }

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

        public event PropertyChangedEventHandler PropertyChanged;

        public abstract void LoadDescriptor();

        public virtual bool SupportsConfiguration(BuildConfiguration configuration)
        {
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

    }
}
