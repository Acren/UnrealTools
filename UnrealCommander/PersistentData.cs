using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using UnrealAutomationCommon;
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

        private bool _hasFinishedLoading;
        private OperationParameters _operationParameters;

        private Type _operationType;

        public PersistentData()
        {
            Targets = new ObservableCollection<IOperationTarget>();
            Targets.CollectionChanged += (sender, args) =>
            {
                OnPropertyChanged(nameof(Targets));
            };
            OperationParameters = new OperationParameters();
            OperationType = typeof(LaunchEditor);
        }

        [OnDeserialized]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            IEnumerable<IOperationTarget> invalidTargets = Targets.Where(x => !x.IsValid).ToList();
            foreach (IOperationTarget target in invalidTargets)
            {
                AppLogger.Instance.Log($"Removing invalid target '{target.TargetPath}'", LogVerbosity.Warning);
                Targets.Remove(target);
            }
        }

        public ObservableCollection<IOperationTarget> Targets { get; private set; }

        [JsonProperty]
        public OperationParameters OperationParameters
        {
            get => _operationParameters;
            set
            {
                if (_operationParameters != value)
                {
                    if (_operationParameters != null)
                    {
                        _operationParameters.PropertyChanged -= OperationParametersChanged;
                    }

                    _operationParameters = value;
                    if (_operationParameters != null)
                    {
                        _operationParameters.PropertyChanged += OperationParametersChanged;
                    }

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

        public event PropertyChangedEventHandler PropertyChanged;

        public static PersistentData Get()
        {
            return _instance;
        }

        public static PersistentData Load()
        {
            if (File.Exists(dataFilePath))
            {
                using StreamReader sr = new(dataFilePath);
                using JsonReader reader = new JsonTextReader(sr);
                JsonSerializer serializer = new()
                {
                    PreserveReferencesHandling = PreserveReferencesHandling.All,
                    TypeNameHandling = TypeNameHandling.Auto
                };
                _instance = serializer.Deserialize<PersistentData>(reader);
            }

            if (_instance == null)
            {
                _instance = new PersistentData();
            }

            _instance._hasFinishedLoading = true;

            return _instance;
        }

        private static void Save()
        {
            using StreamWriter sw = new(dataFilePath);
            using JsonTextWriter writer = new(sw) { Formatting = Formatting.Indented };
            JsonSerializer serializer = new()
            {
                PreserveReferencesHandling = PreserveReferencesHandling.All,
                TypeNameHandling = TypeNameHandling.Auto
            };
            serializer.Serialize(writer, _instance);
        }

        public IOperationTarget AddTarget(string path)
        {
            IOperationTarget target;
            if (ProjectPaths.Instance.IsTargetFile(path))
            {
                target = new Project(path);
            }
            else if (PluginPaths.Instance.IsTargetFile(path))
            {
                target = new Plugin(path);
            }
            else if (PackagePaths.Instance.IsTargetFile(path))
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