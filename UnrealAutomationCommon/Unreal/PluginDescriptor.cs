using System.IO;
using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    public class PluginDescriptor
    {
        public string VersionName { get; set; }
        public string FriendlyName { get; set; }
        public string EngineVersion { get; set; }

        public string TrimmedEngineVersion => GetEngineInstall().Key;

        public static PluginDescriptor Load(string uPluginPath)
        {
            return JsonConvert.DeserializeObject<PluginDescriptor>(File.ReadAllText(uPluginPath));
        }

        public EngineInstall GetEngineInstall()
        {
            EngineInstall engine = EngineInstallFinder.GetEngineInstall(EngineVersion);
            if (engine != null)
            {
                return engine;
            }

            EngineInstallVersion version = new EngineInstallVersion(EngineVersion);

            return EngineInstallFinder.GetEngineInstall(version);
        }

        public string GetEngineInstallDirectory()
        {
            return GetEngineInstall().InstallDirectory;
        }

        public string GetRunUATPath()
        {
            return EnginePaths.GetRunUATPath(GetEngineInstall());
        }
    }
}
