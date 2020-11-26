using Newtonsoft.Json;
using System.IO;

namespace UnrealAutomationCommon
{
    public class PluginDescriptor
    {
        public string VersionName { get; set; }
        public string FriendlyName { get; set; }
        public string EngineVersion { get; set; }

        public string TrimmedEngineVersion
        {
            get
            {
                string[] VerStrings = EngineVersion.Split('.');
                if (VerStrings.Length < 2)
                {
                    return EngineVersion;
                }
                string MajorMinor = VerStrings[0] + '.' + VerStrings[1];
                if (VerStrings.Length > 2 && VerStrings[2] != "0")
                {
                    return MajorMinor + '.' + VerStrings[2];
                }
                return MajorMinor;
            }
        }

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
