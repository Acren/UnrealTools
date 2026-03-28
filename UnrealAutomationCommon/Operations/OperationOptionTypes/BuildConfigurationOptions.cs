using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class BuildConfigurationOptions : OperationOptions
    {
        public override int SortIndex => 20;

        [ObservableProperty]
        [property: DisplayName("Configuration")]
        [property: Description("Chooses the Unreal build configuration used for compile-oriented operations.")]
        private BuildConfiguration configuration = BuildConfiguration.Development;
    }
}
