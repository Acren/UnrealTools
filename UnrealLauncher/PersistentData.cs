using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.IO;
using UnrealAutomationCommon;

namespace UnrealLauncher
{
    class PersistentData
    {
        private static string dataFilePath = "data.json";

        private static PersistentData instance;

        public ObservableCollection<Project> Projects { get; set; }

        public PersistentData()
        {
            Projects = new ObservableCollection<Project>();
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
            Project NewProject = new Project(Path);
            Projects.Add(NewProject);
            Save();
            return NewProject;
        }
    }
}
