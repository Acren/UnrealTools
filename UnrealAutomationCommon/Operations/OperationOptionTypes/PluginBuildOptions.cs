namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginBuildOptions : OperationOptions
    {
        public Option<bool> StrictIncludes { get; } = false;
    }
}