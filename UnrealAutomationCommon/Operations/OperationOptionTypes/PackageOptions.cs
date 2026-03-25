using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class PackageOptions : OperationOptions
    {
        public override int SortIndex => 60;

        [ObservableProperty]
        private bool noDebugInfo = false;

        [ObservableProperty]
        private bool archive = true;
    }
}
