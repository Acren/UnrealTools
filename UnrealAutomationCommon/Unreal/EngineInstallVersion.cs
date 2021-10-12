using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    public class EngineInstallVersion
    {
        public EngineInstallVersion()
        {

        }

        public EngineInstallVersion(string versionString)
        {
            string[] verStrings = versionString.Split('.');

            MajorVersion = int.Parse(verStrings[0]);
            MinorVersion = 0;
            PatchVersion = 0;

            if (verStrings.Length >= 1)
            {
                MinorVersion = int.Parse(verStrings[1]);
                if (verStrings.Length >= 2)
                {
                    PatchVersion = int.Parse(verStrings[2]);
                }
            }
        }

        public EngineInstallVersion(FileVersionInfo fileVersion)
        {
            MajorVersion = fileVersion.FileMajorPart;
            MinorVersion = fileVersion.FileMinorPart;
            PatchVersion = fileVersion.FileBuildPart;
        }

        public int MajorVersion { get; set; }
        public int MinorVersion { get; set; }
        public int PatchVersion { get; set; }

        public string MajorMinorString => $"{MajorVersion}.{MinorVersion}";

        public override string ToString()
        {
            return $"{MajorVersion}.{MinorVersion}.{PatchVersion}";
        }

        public static EngineInstallVersion Load(string buildVersionPath)
        {
            if (!File.Exists(buildVersionPath)) return null;
            return JsonConvert.DeserializeObject<EngineInstallVersion>(File.ReadAllText(buildVersionPath));
        }

        public bool MinorVersionEquals(EngineInstallVersion other)
        {
            return MajorVersion == other.MajorVersion && MinorVersion == other.MinorVersion;
        }

        public EngineInstallVersion WithPatch(int patchVersion)
        {
            EngineInstallVersion newVersion = new ()
            {
                MajorVersion = MajorVersion,
                MinorVersion = MinorVersion,
                PatchVersion = patchVersion
            };
            return newVersion;
        }

    }
}