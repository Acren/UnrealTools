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

        public EngineInstallVersion(int majorVersion, int minorVersion, int patchVersion = 0)
        {
            MajorVersion = majorVersion;
            MinorVersion = minorVersion;
            PatchVersion = patchVersion;
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
            EngineInstallVersion newVersion = new()
            {
                MajorVersion = MajorVersion,
                MinorVersion = MinorVersion,
                PatchVersion = patchVersion
            };
            return newVersion;
        }

        public static bool operator ==(EngineInstallVersion a, EngineInstallVersion b)
        {
            bool aNull = ReferenceEquals(a, null);
            bool bNull = ReferenceEquals(b, null);
            if (aNull && bNull)
            {
                return true;
            }

            if (aNull || bNull)
            {
                return false;
            }

            return a.MajorVersion == b.MajorVersion
            && a.MinorVersion == b.MinorVersion
            && a.PatchVersion == b.PatchVersion;
        }

        public static bool operator !=(EngineInstallVersion a, EngineInstallVersion b)
        {
            return !(a == b);
        }

        public static bool operator <(EngineInstallVersion a, EngineInstallVersion b)
        {
            if (a.MajorVersion != b.MajorVersion)
            {
                return a.MajorVersion < b.MajorVersion;
            }

            if (a.MinorVersion != b.MinorVersion)
            {
                return a.MinorVersion < b.MinorVersion;
            }

            return a.PatchVersion < b.PatchVersion;
        }

        public static bool operator >(EngineInstallVersion a, EngineInstallVersion b)
        {
            return b < a;
        }

        public static bool operator <=(EngineInstallVersion a, EngineInstallVersion b)
        {
            return a < b || a == b;
        }

        public static bool operator >=(EngineInstallVersion a, EngineInstallVersion b)
        {
            return a > b || a == b;
        }
    }
}