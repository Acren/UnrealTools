using global::LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PackageOptions : global::LocalAutomation.Runtime.OperationOptions
    {
        public override int SortIndex => 60;
        public global::LocalAutomation.Runtime.Option<bool> NoDebugInfo { get; } = false;
        public global::LocalAutomation.Runtime.Option<bool> Archive { get; } = true;
    }
}
