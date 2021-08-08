using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public interface IOperationTarget
    {
        public string Name { get;}
        public EngineInstall EngineInstall { get; }
    }

    public abstract class OperationTarget : IOperationTarget, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public abstract string Name { get; }
        public abstract EngineInstall EngineInstall { get; }

        public abstract void LoadDescriptor();

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
