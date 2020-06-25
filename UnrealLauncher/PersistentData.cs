using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using UnrealAutomationCommon;

namespace UnrealLauncher
{
    class PersistentData
    {
        private static string dataFilePath = "data.json";

        private static PersistentData instance;

        public ObservableCollection<Project> Projects { get; set; }
        public ObservableCollection<Plugin> Plugins { get; set; }

        public PersistentData()
        {
            Projects = new ObservableCollection<Project>();
            Plugins = new ObservableCollection<Plugin>();
        }

        public static PersistentData Get() { return instance; }

        public static void Load()
        {
            if(File.Exists(dataFilePath))
            {
                using (StreamReader sr = new StreamReader(dataFilePath))
                {
                    using (JsonReader reader = new JsonTextReader(sr))
                    {
                        JsonSerializer serializer = new JsonSerializer();
                        instance = serializer.Deserialize<PersistentData>(reader);
                    }
                }
            }
            else
            {
                instance = new PersistentData();
            }
            foreach(Project Project in instance.Projects)
            {
                Project.LoadDescriptor();
            }

            foreach (Plugin Plugin in instance.Plugins)
            {
                Plugin.LoadDescriptor();
            }
        }

        public static void Save()
        {
            using (StreamWriter sw = new StreamWriter(dataFilePath))
            {
                using (JsonWriter writer = new JsonTextWriter(sw))
                {
                    JsonSerializer serializer = new JsonSerializer();
                    serializer.Serialize(writer, instance);
                }
            }
        }

        public Project AddProject(string Path)
        {
            if (GetProject(Path) != null)
            {
                return null;
            }

            Project NewProject = new Project(Path);
            Projects.Add(NewProject);
            Save();
            return NewProject;
        }

        public Plugin AddPlugin(string Path)
        {
            if (GetPlugin(Path) != null)
            {
                return null;
            }

            Plugin NewPlugin = new Plugin(Path);
            Plugins.Add(NewPlugin);
            Save();
            return NewPlugin;
        }

        public Project GetProject(string Path)
        {
            return Projects.FirstOrDefault(p => p.UProjectPath == Path);
        }

        public Plugin GetPlugin(string Path)
        {
            return Plugins.FirstOrDefault(p => p.UPluginPath == Path);
        }
    }
}
