namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class AutomationOptions : OperationOptions
    {
        public AutomationOptions()
        {
            RunTests = new Option<bool>(OptionChanged, false);
            Headless = new Option<bool>(OptionChanged, true);
        }

        public Option<bool> RunTests { get; }
        public Option<bool> Headless { get; }

        public string TestNameOverride { get; set; }
    }
}