using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using LocalAutomation.Persistence;
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
        private static readonly ISerializationBinder SerializationBinder = new DefaultSerializationBinder();
        private static readonly JsonFileStateStore<PersistentData> StateStore = CreateStateStore();

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

                return App.Services.Operations.GetAvailableOperationTypes(target).ToList();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public static PersistentData Get()
        {
            return _instance;
        }

        public static PersistentData Load()
        {
            JsonStateLoadResult<PersistentData> loadResult = StateStore.Load();
            _instance = loadResult.State;

            _instance._hasFinishedLoading = true;

            // Persist the cleaned data immediately so stale type references stop breaking future startups.
            if (loadResult.WasModified)
            {
                _instance.Save();
            }

            return _instance;
        }

        private void Save()
        {
            if(_isSaving)
            {
                throw new Exception("Already saving");
            }

            _isSaving = true;

            StateStore.Save(this);

            _isSaving = false;
        }

        // Centralize the JSON file I/O in LocalAutomation.Persistence while keeping the existing serializer settings
        // and type-sanitization behavior specific to this state model.
        private static JsonFileStateStore<PersistentData> CreateStateStore()
        {
            return new JsonFileStateStore<PersistentData>(
                filePath: DataFilePath,
                createDefaultState: () => new PersistentData(),
                createSerializer: CreateSerializer,
                preprocessToken: jsonToken => RemoveUnknownTypedObjects(jsonToken, "$", isRoot: true),
                onLoadException: ex =>
                {
                    AppLogger.LoggerInstance.LogError("Failed to load persistent data");
                    AppLogger.LoggerInstance.LogError(ex.ToString());
                });
        }

        // Reuse the same serializer settings for load and save so typed references behave consistently.
        private static JsonSerializer CreateSerializer()
        {
            return new JsonSerializer
            {
                PreserveReferencesHandling = PreserveReferencesHandling.All,
                TypeNameHandling = TypeNameHandling.Auto,
                SerializationBinder = SerializationBinder
            };
        }

        // Recursively drop any object whose declared Json.NET type can no longer be resolved in the current app.
        private static bool RemoveUnknownTypedObjects(JToken token, string path, bool isRoot = false)
        {
            bool removedAny = false;

            if (token is JObject obj)
            {
                JToken typeToken = obj["$type"];
                if (typeToken?.Type == JTokenType.String)
                {
                    string qualifiedTypeName = typeToken.Value<string>();
                    if (!CanResolveTypeName(qualifiedTypeName))
                    {
                        AppLogger.LoggerInstance.LogWarning("Dropping persisted object at '{Path}' with unknown type '{TypeName}'", path, qualifiedTypeName);

                        if (isRoot)
                        {
                            obj.RemoveAll();
                            return true;
                        }

                        obj.Remove();
                        return true;
                    }
                }

                foreach (JProperty property in obj.Properties().ToList())
                {
                    removedAny |= RemoveUnknownTypedObjects(property.Value, AppendJsonPath(path, property.Name));
                }

                return removedAny;
            }

            if (token is JArray array)
            {
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    removedAny |= RemoveUnknownTypedObjects(array[i], AppendJsonPath(path, i.ToString()), isRoot: false);
                }
            }

            return removedAny;
        }

        // Let Json.NET parse the assembly-qualified type string so sanitization matches runtime deserialization behavior.
        private static bool CanResolveTypeName(string qualifiedTypeName)
        {
            try
            {
                int assemblySeparatorIndex = qualifiedTypeName.IndexOf(',');
                string typeName = assemblySeparatorIndex >= 0 ? qualifiedTypeName.Substring(0, assemblySeparatorIndex).Trim() : qualifiedTypeName.Trim();
                string assemblyName = assemblySeparatorIndex >= 0 ? qualifiedTypeName.Substring(assemblySeparatorIndex + 1).Trim() : null;
                Type resolvedType = SerializationBinder.BindToType(assemblyName, typeName);
                return resolvedType != null;
            }
            catch
            {
                return false;
            }
        }

        // Keep log paths readable for both object properties and array items while walking the JSON tree.
        private static string AppendJsonPath(string path, string segment)
        {
            return int.TryParse(segment, out _)
                ? $"{path}[{segment}]"
                : $"{path}.{segment}";
        }

        public IOperationTarget AddTarget(string path)
        {
            object createdTarget = App.Services.Targets.CreateTarget(path);
            if (createdTarget is not IOperationTarget target)
            {
                throw new Exception($"Created target '{createdTarget.GetType().Name}' does not implement IOperationTarget");
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
