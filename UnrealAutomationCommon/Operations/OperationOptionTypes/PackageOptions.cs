namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PackageOptions : OperationOptions
    {
        public override int SortIndex => 60;
        public Option<bool> NoDebugInfo { get; } = false;
    }
}