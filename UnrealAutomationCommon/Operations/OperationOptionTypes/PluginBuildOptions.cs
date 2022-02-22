namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginBuildOptions : OperationOptions
    {
        public override int Index => 70;

        public Option<bool> BuildWin64 { get; } = true;
        public Option<bool> BuildLinux { get; } = false;
        public Option<bool> StrictIncludes { get; } = false;
    }
}