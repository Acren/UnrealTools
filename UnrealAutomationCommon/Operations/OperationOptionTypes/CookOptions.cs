using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class CookOptions : OperationOptions
    {
        public override int SortIndex => 30;

        public override string Name => "Cooker";

        [ObservableProperty]
        private BuildConfiguration cookerConfiguration = BuildConfiguration.Development;

        [ObservableProperty]
        private bool waitForAttach = false;
    }
}
