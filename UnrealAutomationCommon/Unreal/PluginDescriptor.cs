using Newtonsoft.Json;
using System;
using System.IO;
using Semver;

namespace UnrealAutomationCommon.Unreal
{
    public class PluginDescriptor
    {
        public string VersionName { get; set; }
        public string FriendlyName { get; set; }
        public bool IsBetaVersion { get; set; }
        public string EngineVersionString { get; set; }

        public SemVersion SemVersion => SemVersion.Parse(VersionName, SemVersionStyles.Strict);
        public EngineVersion EngineVersion => string.IsNullOrEmpty(EngineVersionString) ? null : new(EngineVersionString);

        public static PluginDescriptor Load(string uPluginPath)
        {
            FileUtils.WaitForFileReadable(uPluginPath);
            try
            {
                return JsonConvert.DeserializeObject<PluginDescriptor>(File.ReadAllText(uPluginPath));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}