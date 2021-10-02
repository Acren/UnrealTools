namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class AutomationOptions : OperationOptions
    {
        public Option<bool> RunTests { get; }
        public Option<bool> Headless { get; }

        public AutomationOptions()
        {
            RunTests = new Option<bool>(OptionChanged, false);
            Headless = new Option<bool>(OptionChanged, true);
        }

        public string TestNameOverride { get; set; }
    }
}
