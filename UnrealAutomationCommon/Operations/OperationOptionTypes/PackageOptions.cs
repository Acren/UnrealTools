namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PackageOptions : OperationOptions
    {
        public Option<bool> NoDebugInfo { get; } = false;
    }
}