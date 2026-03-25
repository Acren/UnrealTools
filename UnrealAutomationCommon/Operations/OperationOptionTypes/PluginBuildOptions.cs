using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class PluginBuildOptions : OperationOptions
    {
        public override int SortIndex => 45;

        [ObservableProperty]
        private bool buildWin64 = true;

        [ObservableProperty]
        private bool buildLinux = true;

        [ObservableProperty]
        private bool strictIncludes = true;
    }
}
