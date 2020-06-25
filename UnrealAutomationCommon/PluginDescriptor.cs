using Newtonsoft.Json;
using System.IO;

namespace UnrealAutomationCommon
{
    public class PluginDescriptor
    {
        public string VersionName { get; set; }
        public string FriendlyName { get; set; }
        public string EngineVersion { get; set; }

        public static PluginDescriptor Load(string uPluginPath)
        {
            return JsonConvert.DeserializeObject<PluginDescriptor>(File.ReadAllText(uPluginPath));
        }

        public string GetEngineInstallDirectory()
        {
            return ProjectUtils.GetEngineInstallDirectory(EngineVersion);
        }

        public string GetRunUATPath()
        {
            return EnginePaths.GetRunUATPath(GetEngineInstallDirectory());
        }
    }
}
