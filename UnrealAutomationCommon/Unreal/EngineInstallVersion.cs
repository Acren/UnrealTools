using System;
using Newtonsoft.Json;
using System.Diagnostics;
using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    [JsonObject(MemberSerialization.OptIn)]
    public class EngineInstallVersion : IEquatable<EngineInstallVersion>
    {
        public override int GetHashCode()
        {
            return HashCode.Combine(MajorVersion, MinorVersion, PatchVersion);
        }

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

        [JsonProperty]
        public int MajorVersion { get; set; }
        [JsonProperty]
        public int MinorVersion { get; set; }
        [JsonProperty]
        public int PatchVersion { get; set; }

        public string MajorMinorString => $"{MajorVersion}.{MinorVersion}";

        public override string ToString()
        {
            return $"{MajorVersion}.{MinorVersion}.{PatchVersion}";
        }

        public static EngineInstallVersion Load(string buildVersionPath)
        {
            if (!File.Exists(buildVersionPath))
            {
                return null;
            }

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
            if (a is null)
            {
                return b is null;
            }
            return a.Equals(b);
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

        public bool Equals(EngineInstallVersion other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return MajorVersion == other.MajorVersion && MinorVersion == other.MinorVersion && PatchVersion == other.PatchVersion;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            if (obj.GetType() != this.GetType())
            {
                return false;
            }

            return Equals((EngineInstallVersion)obj);
        }
    }
}