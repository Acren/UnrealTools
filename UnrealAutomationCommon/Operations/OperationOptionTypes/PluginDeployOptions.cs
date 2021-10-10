namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginDeployOptions : OperationOptions
    {
        public PluginDeployOptions()
        {
            ArchivePath = new Option<string>(OptionChanged, null);
        }

        public Option<string> ArchivePath { get; }
    }
}