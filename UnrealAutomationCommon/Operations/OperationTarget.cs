using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public interface IOperationTarget : INotifyPropertyChanged
    {
        public string Name { get; }
        public string TargetPath { get; }
        public string TestName { get; set; }

        public string OutputPath { get; }
        public string TypeName { get; }

        public bool SupportsConfiguration(BuildConfiguration configuration);
    }

    public abstract class OperationTarget : IOperationTarget
    {
        public abstract string Name { get; }
        public abstract string TargetPath { get; }

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

        public string OutputPath => Path.Combine("C:/UnrealCommander/", Name.Replace(" ", ""));
        public string TypeName => GetType().Name.SplitWordsByUppercase();

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
