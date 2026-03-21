using global::LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class AutomationOptions : global::LocalAutomation.Runtime.OperationOptions
    {
        public override int SortIndex => 50;

        public global::LocalAutomation.Runtime.Option<bool> RunTests { get; } = false;
        public global::LocalAutomation.Runtime.Option<bool> Headless { get; } = true;

        public string TestNameOverride { get; set; }
    }
}
