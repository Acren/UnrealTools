using UnrealAutomationCommon.Unreal;
using LocalAutomation.Runtime;


namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class CookOptions : OperationOptions
    {
        public override int SortIndex => 30;
        public override string Name => "Cooker";

        public Option<BuildConfiguration> CookerConfiguration { get; } = BuildConfiguration.Development;

        public Option<bool> WaitForAttach { get; } = false;
    }
}
