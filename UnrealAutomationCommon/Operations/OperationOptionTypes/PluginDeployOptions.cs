namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginDeployOptions : OperationOptions
    {
        public PluginDeployOptions()
        {
            ArchivePath = AddOption<string>(null);
            ArchivePluginBuild = AddOption(false);
            ArchiveExampleProject = AddOption(false);
            ArchiveDemoPackage = AddOption(false);
            TestPackage = AddOption(true);
        }

        public Option<string> ArchivePath { get; }
        public Option<bool> ArchivePluginBuild { get; }
        public Option<bool> ArchiveExampleProject { get; }
        public Option<bool> ArchiveDemoPackage { get; }
        public Option<bool> TestPackage { get; }
    }
}