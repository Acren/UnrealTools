using Newtonsoft.Json;
using System;
using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public class PluginDescriptor
    {
        public string VersionName { get; set; }
        public string FriendlyName { get; set; }
        public bool IsBetaVersion { get; set; }
        public string EngineVersion { get; set; }

        public EngineInstallVersion EngineInstallVersion => string.IsNullOrEmpty(EngineVersion) ? null : new(EngineVersion);

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