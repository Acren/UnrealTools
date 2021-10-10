namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PackageOptions : OperationOptions
    {
        public PackageOptions()
        {
            NoDebugInfo = new Option<bool>(OptionChanged, false);
        }

        public Option<bool> NoDebugInfo { get; }
    }
}