using System;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
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

        private bool _hasFinishedLoading = false;

        private Type _operationType;
        private OperationParameters _operationParameters;

        public ObservableCollection<Project> Projects { get; private set; }
        public ObservableCollection<Plugin> Plugins { get; private set; }

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
                    //if(_operationType)
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
            Projects = new ObservableCollection<Project>();
            Plugins = new ObservableCollection<Plugin>();
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
                    TypeNameHandling = TypeNameHandling.All
                };
                _instance = serializer.Deserialize<PersistentData>(reader);
            }

            if(_instance == null)
            {
                _instance = new PersistentData();
            }

            foreach(Project project in _instance.Projects)
            {
                project.LoadDescriptor();
            }

            foreach (Plugin plugin in _instance.Plugins)
            {
                plugin.LoadDescriptor();
            }

            _instance._hasFinishedLoading = true;

            return _instance;
        }

        private static void Save()
        {
            using StreamWriter sw = new StreamWriter(dataFilePath);
            using JsonTextWriter writer = new JsonTextWriter(sw) {Formatting = Formatting.Indented};
            JsonSerializer serializer = new JsonSerializer
            {
                PreserveReferencesHandling = PreserveReferencesHandling.All,
                TypeNameHandling = TypeNameHandling.All
            };
            serializer.Serialize(writer, _instance);
        }

        public Project AddProject(string path)
        {
            if (GetProject(path) != null)
            {
                return null;
            }

            Project newProject = new Project(path);
            Projects.Add(newProject);
            Save();
            return newProject;
        }

        public Plugin AddPlugin(string path)
        {
            if (GetPlugin(path) != null)
            {
                return null;
            }

            Plugin newPlugin = new Plugin(path);
            Plugins.Add(newPlugin);
            Save();
            return newPlugin;
        }

        public void RemoveProject(Project project)
        {
            Projects.Remove(project);
            Save();
        }

        public void RemovePlugin(Plugin plugin)
        {
            Plugins.Remove(plugin);
            Save();
        }

        public Project GetProject(string path)
        {
            return Projects.FirstOrDefault(p => p.UProjectPath == path);
        }

        public Plugin GetPlugin(string path)
        {
            return Plugins.FirstOrDefault(p => p.UPluginPath == path);
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
