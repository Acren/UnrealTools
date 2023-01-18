namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginBuildOptions : OperationOptions
    {
        public override int SortIndex => 45;

        public Option<bool> BuildWin64 { get; set; } = true;
        public Option<bool> BuildLinux { get; set; } = true;
        public Option<bool> StrictIncludes { get; set; } = true;
    }
}