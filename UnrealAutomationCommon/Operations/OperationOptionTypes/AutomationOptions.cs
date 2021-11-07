namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class AutomationOptions : OperationOptions
    {
        public Option<bool> RunTests { get; } = false;
        public Option<bool> Headless { get; } = true;

        public string TestNameOverride { get; set; }
    }
}