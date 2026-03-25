using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class BuildConfigurationOptions : OperationOptions
    {
        public override int SortIndex => 20;

        [ObservableProperty]
        private BuildConfiguration configuration = BuildConfiguration.Development;
    }
}
