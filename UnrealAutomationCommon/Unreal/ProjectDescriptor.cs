using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon.Unreal
{
    public class ProjectModule
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    public class ProjectPluginDependency
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }

    public class ProjectDescriptor
    {
        private string _engineAssociation;

        public string EngineAssociation
        {
            get => _engineAssociation;
            set
            {
                _engineAssociation = value;
                Engine = EngineFinder.GetEngineInstall(EngineAssociation, true);
            }
        }

        public List<ProjectModule> Modules { get; set; }
        public List<ProjectPluginDependency> Plugins { get; set; }

        public string EngineFriendlyName
        {
            get
            {
                if (IsEngineInstalled())
                {
                    return EngineAssociation;
                }

                if (Engine == null)
                {
                    return EngineAssociation;
                }

                return Engine.InstallDirectory;
            }
        }

        public Engine Engine { get; private set; }

        public string EditorTargetName
        {
            get
            {
                // Try find an editor module
                var editorModule = Modules.Find(m => m.Type == "Editor");
                if (editorModule != null)
                {
                    return editorModule.Name;
                }

                // If there is no explicitly defined editor module, append "Editor" to the first module
                return Modules[0].Name + "Editor";
            }
        }

        public static ProjectDescriptor Load(string uProjectPath)
        {
            return JsonConvert.DeserializeObject<ProjectDescriptor>(File.ReadAllText(uProjectPath));
        }

        public bool IsEngineInstalled()
        {
            return !EngineAssociation.StartsWith("{");
        }

        public bool HasPluginEnabled(string PluginName)
        {
            return Plugins.Any(Plugin => Plugin.Name == PluginName && Plugin.Enabled);
        }
    }
}