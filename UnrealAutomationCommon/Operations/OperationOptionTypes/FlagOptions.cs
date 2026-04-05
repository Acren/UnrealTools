using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class FlagOptions : OperationOptions
    {
        public override int SortIndex => 40;

        public override string Name => "Flags";

        [ObservableProperty]
        [property: DisplayName("StompMalloc")]
        [property: Description("Enables Unreal's stomp allocator diagnostics to help catch memory corruption issues.")]
        private bool stompMalloc = false;

        [ObservableProperty]
        [property: DisplayName("Wait For Attach")]
        [property: Description("Pauses startup long enough for a debugger to attach before the process continues.")]
        private bool waitForAttach = false;
    }
}
