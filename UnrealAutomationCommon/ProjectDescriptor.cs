using System.Collections.Generic;
using Newtonsoft.Json;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon
{
    public class ProjectPluginDependency
    {
        public string Name { get; set; }
        public bool Enabled { get; set; }
    }

    public class ProjectDescriptor
    {
        public string EngineAssociation { get; set; }
        
        public List<ProjectPluginDependency> Plugins { get; set; }

        public string EngineFriendlyName => IsEngineInstalled() ? EngineAssociation : GetEngineInstallDirectory();

        public static ProjectDescriptor Load(string uProjectPath)
        {
            return JsonConvert.DeserializeObject<ProjectDescriptor>(File.ReadAllText(uProjectPath));
        }

        public string GetEngineInstallDirectory()
        {
            return ProjectUtils.GetEngineInstallDirectory(EngineAssociation);
        }

        public bool IsEngineInstalled()
        {
            return !EngineAssociation.StartsWith("{");
        }

        public string GetRunUATPath()
        {
            return EnginePaths.GetRunUATPath(GetEngineInstallDirectory());
        }

        public string GetBuildPath()
        {
            return EnginePaths.GetBuildPath(GetEngineInstallDirectory());
        }

        public bool HasPluginEnabled(string PluginName)
        {
            return Plugins.Any(Plugin => Plugin.Name == PluginName && Plugin.Enabled);
        }
    }
}
