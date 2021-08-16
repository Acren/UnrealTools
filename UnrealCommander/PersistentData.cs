using Newtonsoft.Json;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationTypes;
using UnrealAutomationCommon.Unreal;
using UnrealCommander.Annotations;

namespace UnrealCommander
{
    public class PersistentData : INotifyPropertyChanged
    {
        private static readonly string dataFilePath = "data.json";

        private static PersistentData _instance;

        private bool _hasFinishedLoading = false;

        private Type _operationType;
        private OperationParameters _operationParameters;

        public BindingList<IOperationTarget> Targets { get; private set; }

        [JsonProperty]
        public OperationParameters OperationParameters
        {
            get => _operationParameters;
            set
            {
                if (_operationParameters != value)
                {
                    if (_operationParameters != null)
                        _operationParameters.PropertyChanged -= OperationParametersChanged;
                    _operationParameters = value;
                    if (_operationParameters != null)
                        _operationParameters.PropertyChanged += OperationParametersChanged;
                    OperationParametersChanged(this, null);
                }
                void OperationParametersChanged(object sender, PropertyChangedEventArgs args)
                {
                    OnPropertyChanged();
                }
            }
        }

        public Type OperationType
        {
            get => _operationType;
            set
            {
                _operationType = value;
                OnPropertyChanged();
            }
        }

        public PersistentData()
        {
            Targets = new BindingList<IOperationTarget>(){RaiseListChangedEvents = true};
            Targets.ListChanged += (sender, args) => OnPropertyChanged(nameof(Targets));
            OperationParameters = new OperationParameters();
            OperationType = typeof(LaunchEditor);
        }

        public static PersistentData Get() { return _instance; }

        public static PersistentData Load()
        {
            if (File.Exists(dataFilePath))
            {
                using StreamReader sr = new StreamReader(dataFilePath);
                using JsonReader reader = new JsonTextReader(sr);
                JsonSerializer serializer = new JsonSerializer
                {
                    PreserveReferencesHandling = PreserveReferencesHandling.All,
                    TypeNameHandling = TypeNameHandling.Auto
                };
                _instance = serializer.Deserialize<PersistentData>(reader);
            }

            if(_instance == null)
            {
                _instance = new PersistentData();
            }

            _instance._hasFinishedLoading = true;

            return _instance;
        }

        private static void Save()
        {
            using StreamWriter sw = new StreamWriter(dataFilePath);
            using JsonTextWriter writer = new JsonTextWriter(sw) { Formatting = Formatting.Indented };
            JsonSerializer serializer = new JsonSerializer
            {
                PreserveReferencesHandling = PreserveReferencesHandling.All,
                TypeNameHandling = TypeNameHandling.Auto
            };
            serializer.Serialize(writer, _instance);
        }

        public IOperationTarget AddTarget(string path)
        {
            IOperationTarget target;
            if (TargetUtils.IsProjectFile(path))
            {
                target = new Project(path);
            }
            else if (TargetUtils.IsPluginFile(path))
            {
                target = new Plugin(path);
            }
            else if (TargetUtils.IsPackageFile(path))
            {
                target = new Package(path);
            }
            else
            {
                throw new Exception("Path is not a target");
            }
            Targets.Add(target);
            return target;
        }

        public void RemoveTarget(IOperationTarget target)
        {
            Targets.Remove(target);
            Save();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string PropertyName = null)
        {
            if (_hasFinishedLoading)
            {
                Save();
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }
    }
}
