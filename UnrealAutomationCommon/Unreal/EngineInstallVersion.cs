using Newtonsoft.Json;
using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public class EngineInstallVersion
    {
        public int MajorVersion { get; set; }
        public int MinorVersion { get; set; }
        public int PatchVersion { get; set; }

        public EngineInstallVersion()
        {

        }

        public EngineInstallVersion(string versionString)
        {
            string[] verStrings = versionString.Split('.');

            MajorVersion = int.Parse(verStrings[0]);

            if (verStrings.Length >= 1)
            {
                MinorVersion = int.Parse(verStrings[1]);
                if (verStrings.Length >= 2)
                {
                    PatchVersion = int.Parse(verStrings[2]);
                }
            }
        }

        public static EngineInstallVersion Load(string buildVersionPath)
        {
            return JsonConvert.DeserializeObject<EngineInstallVersion>(File.ReadAllText(buildVersionPath));
        }

        public bool MinorVersionEquals(EngineInstallVersion other)
        {
            return MajorVersion == other.MajorVersion && MinorVersion == other.MinorVersion;
        }

    }
}
