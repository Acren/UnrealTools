namespace UnrealAutomationCommon.Unreal
{
    public class EngineInstall
    {
        public string Key { get; set; }
        public string InstallDirectory { get; set; }
        public bool IsSourceBuild = false;

        public EngineInstallVersion GetVersion()
        {
            return EngineInstallVersion.Load(this.GetBuildVersionPath());
        }

        public bool SupportsConfiguration(BuildConfiguration configuration)
        {
            if (configuration == BuildConfiguration.Debug
                || configuration == BuildConfiguration.Test)
            {
                // Only support Debug and Test in source builds
                return IsSourceBuild;
            }

            // Always support DebugGame, Development, Shipping
            return true;
        }

        public string GetWindowsPlatformName()
        {
            if (GetVersion().MajorVersion >= 5)
            {
                return "Windows";
            }

            return "WindowsNoEditor";
        }
    }
}
