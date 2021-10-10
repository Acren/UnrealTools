using System.IO;
using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    public class PluginDescriptor
    {
        public string VersionName { get; set; }
        public string FriendlyName { get; set; }
        public bool IsBetaVersion { get; set; }
        public string EngineVersion { get; set; }

        public EngineInstall EngineInstall
        {
            get
            {
                if (EngineVersion == null) return EngineInstallFinder.GetDefaultEngineInstall();

                EngineInstallVersion version = new(EngineVersion);

                return EngineInstallFinder.GetEngineInstall(version);
            }
        }

        public static PluginDescriptor Load(string uPluginPath)
        {
            FileUtils.WaitForFileReadable(uPluginPath);
            return JsonConvert.DeserializeObject<PluginDescriptor>(File.ReadAllText(uPluginPath));
        }

        public string GetEngineInstallDirectory()
        {
            return EngineInstall.InstallDirectory;
        }

        public string GetRunUATPath()
        {
            return EngineInstall.GetRunUATPath();
        }
    }
}