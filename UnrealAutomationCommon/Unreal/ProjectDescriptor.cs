using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

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

                if (GetEngineInstall() == null)
                {
                    return EngineAssociation;
                }

                return GetEngineInstall().InstallDirectory;
            }
        }

        public static ProjectDescriptor Load(string uProjectPath)
        {
            return JsonConvert.DeserializeObject<ProjectDescriptor>(File.ReadAllText(uProjectPath));
        }

        public EngineInstall GetEngineInstall()
        {
            return EngineInstallFinder.GetEngineInstall(EngineAssociation);
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
