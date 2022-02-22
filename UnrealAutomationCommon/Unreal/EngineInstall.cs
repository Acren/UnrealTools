using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    [JsonObject(MemberSerialization.OptIn)]
    public class EngineInstall
    {
        [JsonProperty]
        public bool IsSourceBuild = false;

        [JsonProperty]
        public string Key { get; set; }
        [JsonProperty]
        public string InstallDirectory { get; set; }

        public string DisplayName => Version.ToString();
        public string LocationString => IsSourceBuild ? InstallDirectory : "Launcher";

        public EngineInstallVersion Version => EngineInstallVersion.Load(this.GetBuildVersionPath());

        public bool SupportsConfiguration(BuildConfiguration configuration)
        {
            if (configuration == BuildConfiguration.Debug
                || configuration == BuildConfiguration.Test)
                // Only support Debug and Test in source builds
            {
                return IsSourceBuild;
            }

            // Always support DebugGame, Development, Shipping
            return true;
        }

        public bool SupportsTestReports => Version >= new EngineInstallVersion(4, 25);

        public string GetWindowsPlatformName()
        {
            if (Version.MajorVersion >= 5)
            {
                return "Windows";
            }

            return "WindowsNoEditor";
        }
    }
}