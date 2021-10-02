using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon.Unreal
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

        public string EngineFriendlyName
        {
            get
            {
                if (IsEngineInstalled())
                {
                    return EngineAssociation;
                }

                if (EngineInstall == null)
                {
                    return EngineAssociation;
                }

                return EngineInstall.InstallDirectory;
            }
        }

        public static ProjectDescriptor Load(string uProjectPath)
        {
            return JsonConvert.DeserializeObject<ProjectDescriptor>(File.ReadAllText(uProjectPath));
        }

        public EngineInstall EngineInstall => EngineInstallFinder.GetEngineInstall(EngineAssociation);

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
