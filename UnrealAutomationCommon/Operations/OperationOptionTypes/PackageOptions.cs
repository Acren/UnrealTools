namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PackageOptions : OperationOptions
    {
        public Option<bool> NoDebugInfo { get; }

        public PackageOptions()
        {
            NoDebugInfo = new Option<bool>(OptionChanged, false);
        }
    }
}
