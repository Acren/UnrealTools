namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginDeployOptions : OperationOptions
    {
        public PluginDeployOptions()
        {
            TestStandalone = AddOption(true);
            TestPackage = AddOption(true);
            ArchivePath = AddOption<string>(null);
            ArchivePluginBuild = AddOption(false);
            ArchiveExampleProject = AddOption(false);
            ArchiveDemoPackage = AddOption(false);
        }

        public Option<bool> TestStandalone { get; }
        public Option<bool> TestPackage { get; }
        public Option<string> ArchivePath { get; }
        public Option<bool> ArchivePluginBuild { get; }
        public Option<bool> ArchiveExampleProject { get; }
        public Option<bool> ArchiveDemoPackage { get; }
    }
}