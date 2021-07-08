using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace UnrealAutomationCommon.Operations
{
    public abstract class OperationTarget : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public EngineInstall EngineInstall => GetEngineInstall();

        public abstract string GetName();
        public abstract EngineInstall GetEngineInstall();
        public abstract void LoadDescriptor();

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
