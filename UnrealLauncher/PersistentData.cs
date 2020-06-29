using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using UnrealAutomationCommon;

namespace UnrealLauncher
{
    class PersistentData
    {
        private static readonly string dataFilePath = "data.json";

        private static PersistentData _instance;

        public ObservableCollection<Project> Projects { get; private set; }
        public ObservableCollection<Plugin> Plugins { get; private set; }

        public PersistentData()
        {
            Projects = new ObservableCollection<Project>();
            Plugins = new ObservableCollection<Plugin>();
        }

        public static PersistentData Get() { return _instance; }

        public static void Load()
        {
            if(File.Exists(dataFilePath))
            {
                using StreamReader sr = new StreamReader(dataFilePath);
                using JsonReader reader = new JsonTextReader(sr);
                JsonSerializer serializer = new JsonSerializer();
                _instance = serializer.Deserialize<PersistentData>(reader);
            }
            else
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
        }

        private static void Save()
        {
            using StreamWriter sw = new StreamWriter(dataFilePath);
            using JsonWriter writer = new JsonTextWriter(sw);
            JsonSerializer serializer = new JsonSerializer();
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
    }
}
