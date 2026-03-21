using global::LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginBuildOptions : global::LocalAutomation.Runtime.OperationOptions
    {
        public override int SortIndex => 45;

        public global::LocalAutomation.Runtime.Option<bool> BuildWin64 { get; set; } = true;
        public global::LocalAutomation.Runtime.Option<bool> BuildLinux { get; set; } = true;
        public global::LocalAutomation.Runtime.Option<bool> StrictIncludes { get; set; } = true;
    }
}
