using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class CookOptions : OperationOptions
    {
        public override int SortIndex => 30;

        public override string Name => "Cooker";

        [ObservableProperty]
        [property: DisplayName("Cooker Configuration")]
        [property: Description("Chooses the configuration used when launching the cooker executable.")]
        private BuildConfiguration cookerConfiguration = BuildConfiguration.Development;

        [ObservableProperty]
        [property: DisplayName("Wait For Attach")]
        [property: Description("Pauses cooker startup long enough for a debugger to attach before execution continues.")]
        private bool waitForAttach = false;
    }
}
