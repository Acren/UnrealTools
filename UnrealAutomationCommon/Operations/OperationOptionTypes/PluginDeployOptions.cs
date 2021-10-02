namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginDeployOptions : OperationOptions
    {
        public Option<string> ArchivePath { get; }

        public PluginDeployOptions()
        {
            ArchivePath = new Option<string>(OptionChanged, null);
        }
    }
}
