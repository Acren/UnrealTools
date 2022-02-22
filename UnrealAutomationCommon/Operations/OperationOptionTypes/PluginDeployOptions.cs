namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginDeployOptions : OperationOptions
    {
        public override int Index => 80;

        public Option<bool> TestStandalone { get; } = true;
        public Option<bool> TestPackage { get; } = true;
        public Option<string> ArchivePath { get; } = "";
        public Option<bool> ArchivePluginBuild { get; } = false;
        public Option<bool> ArchiveExampleProject { get; } = false;
        public Option<bool> ArchiveDemoPackage { get; } = true;
        public Option<bool> IncludeOtherPlugins { get; } = false;
        public Option<string> ExcludePlugins { get; } = "";
    }
}