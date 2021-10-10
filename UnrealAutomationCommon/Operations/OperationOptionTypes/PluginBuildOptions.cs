namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginBuildOptions : OperationOptions
    {
        public PluginBuildOptions()
        {
            StrictIncludes = new Option<bool>(OptionChanged, false);
        }

        public Option<bool> StrictIncludes { get; }
    }
}