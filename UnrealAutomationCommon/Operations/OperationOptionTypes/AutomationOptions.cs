namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class AutomationOptions : OperationOptions
    {
        public override int SortIndex => 50;

        public Option<bool> RunTests { get; } = false;
        public Option<bool> Headless { get; } = true;

        public string TestNameOverride { get; set; }
    }
}