using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class FlagOptions : OperationOptions
    {
        public override int SortIndex => 40;

        public override string Name => "Flags";

        [ObservableProperty]
        private bool stompMalloc = false;

        [ObservableProperty]
        private bool waitForAttach = false;
    }
}
