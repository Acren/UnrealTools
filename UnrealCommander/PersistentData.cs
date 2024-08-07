﻿using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationTypes;
using UnrealAutomationCommon.Unreal;
using UnrealCommander.Annotations;

namespace UnrealCommander
{
    [JsonObject(MemberSerialization.OptIn)]
    public class PersistentData : INotifyPropertyChanged
    {
        private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnrealCommander");
        private static readonly string DataFilePath = Path.Combine(DataFolder, "data.json");

        private static PersistentData _instance;

        private bool _hasFinishedLoading;
        private OperationParameters _operationParameters;

        private Type _operationType;

        private bool _isSaving = false;

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
                AppLogger.LoggerInstance.LogWarning($"Removing invalid target '{target.TargetPath}'");
                Targets.Remove(target);
            }
        }

        [JsonProperty]
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
                    OnPropertyChanged(nameof(AvailableOperationTypes));
                }
            }
        }

        [JsonProperty]
        public Type OperationType
        {
            get => _operationType;
            set
            {
                if (_operationType != value)
                {
                    _operationType = value;
                    OnPropertyChanged();
                }
            }
        }

        [JsonProperty]
        private Dictionary<string, string> ProgramPaths { get; } = new();

        public string GetProgramPath(string programKey)
        {
            return ProgramPaths.TryGetValue(programKey, out var path) ? path : null;
        }

        public void SetProgramPath(string programKey, string path)
        {
            ProgramPaths[programKey] = path;

            OnPropertyChanged(nameof(ProgramPaths));
        }

        public List<Type> AvailableOperationTypes
        {
            get
            {
                var target = OperationParameters.Target;
                if (target == null)
                {
                    return new();
                }
                var availableOperationTypes = OperationList.GetOrderedOperationTypes().Where(o => Operation.OperationTypeSupportsTarget(o, target)).ToList();
                return availableOperationTypes;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public static PersistentData Get()
        {
            return _instance;
        }

        public static PersistentData Load()
        {
            if (File.Exists(DataFilePath))
            {
                using StreamReader sr = new(DataFilePath);
                using JsonReader reader = new JsonTextReader(sr);
                JsonSerializer serializer = new()
                {
                    PreserveReferencesHandling = PreserveReferencesHandling.All,
                    TypeNameHandling = TypeNameHandling.Auto
                };

                try
                {
                    _instance = serializer.Deserialize<PersistentData>(reader);
                }
                catch (Exception ex)
                {
                    AppLogger.LoggerInstance.LogError("Failed to load persistent data");
                    AppLogger.LoggerInstance.LogError(ex.ToString());
                }
            }

            if (_instance == null)
            {
                _instance = new PersistentData();
            }

            _instance._hasFinishedLoading = true;

            return _instance;
        }

        private void Save()
        {
            if(_isSaving)
            {
                throw new Exception("Already saving");
            }

            _isSaving = true;

            // Build the entire json string before writing the file
            // This is so that if we encounter an exception during serialization, we aren't left with a corrupt file

            StringBuilder sb = new ();
            StringWriter sw = new (sb);

            using JsonTextWriter writer = new(sw) { Formatting = Formatting.Indented };
            JsonSerializer serializer = new()
            {
                PreserveReferencesHandling = PreserveReferencesHandling.All,
                TypeNameHandling = TypeNameHandling.Auto
            };

            serializer.Serialize(writer, this);

            string jsonString = sb.ToString();

            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(DataFilePath, jsonString);

            _isSaving = false;
        }

        public IOperationTarget AddTarget(string path)
        {
            IOperationTarget target;
            if (ProjectPaths.Instance.IsTargetDirectory(path))
            {
                target = new Project(path);
            }
            else if (PluginPaths.Instance.IsTargetDirectory(path))
            {
                target = new Plugin(path);
            }
            else if (PackagePaths.Instance.IsTargetDirectory(path))
            {
                target = new Package(path);
            }
            else if (EnginePaths.Instance.IsTargetDirectory(path))
            {
                target = new Engine(path);
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
            if (_hasFinishedLoading && !_isSaving)
            {
                Save();
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
        }
    }
}