using Newtonsoft.Json;
using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public class EngineInstallVersion
    {
        public int MajorVersion { get; set; }
        public int MinorVersion { get; set; }
        public int PatchVersion { get; set; }

        public static EngineInstallVersion Load(string buildVersionPath)
        {
            return JsonConvert.DeserializeObject<EngineInstallVersion>(File.ReadAllText(buildVersionPath));
        }
    }
}
