using UnrealAutomationCommon.Unreal;
using LocalAutomation.Runtime;


namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class BuildConfigurationOptions : OperationOptions
    {
        public override int SortIndex => 20;

        public Option<BuildConfiguration> Configuration { get; } = BuildConfiguration.Development;
    }
}
