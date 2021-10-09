using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public interface IOperationTarget : INotifyPropertyChanged
    {
        public string Name { get; }
        public string TargetPath { get; }
        public string TargetDirectory { get; }

        public string OutputDirectory { get; }
        public string TypeName { get; }

        public string TestName { get; set; }

        public bool IsValid { get; }

        public bool SupportsConfiguration(BuildConfiguration configuration);
    }

    [JsonObject(MemberSerialization.OptIn)]
    public abstract class OperationTarget : IOperationTarget
    {
        public abstract string Name { get; }
        public abstract string TargetPath { get; }
        public string TargetDirectory => Path.GetDirectoryName(TargetPath);

        private string _testName = string.Empty;

        [JsonProperty]
        public string TestName
        {
            get => _testName;
            set
            {
                _testName = value;
                OnPropertyChanged();
            }
        }

        public string OutputDirectory => Path.Combine(OutputPaths.Root(), Name.Replace(" ", ""));
        public string TypeName => GetType().Name.SplitWordsByUppercase();

        public virtual bool IsValid => true;

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

        public override bool Equals(object other)
        {
            if (other == null || other.GetType() != GetType())
            {
                return false;
            }

            return (other as OperationTarget).TargetPath == TargetPath;
        }
    }
}
