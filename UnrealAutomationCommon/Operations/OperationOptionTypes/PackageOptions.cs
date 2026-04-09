using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class PackageOptions : OperationOptions
    {
        public override int SortIndex => 60;

        [ObservableProperty]
        [property: DisplayName("Build")]
        [property: Description("Includes BuildCookRun's build step during packaging. Turn this off only when the package flow can reuse existing binaries or the target no longer has code to compile.")]
        private bool build = true;

        [ObservableProperty]
        [property: DisplayName("No Debug Info")]
        [property: Description("Omits debug symbols and related debug information from the packaged output.")]
        private bool noDebugInfo = false;

        [ObservableProperty]
        [property: DisplayName("Archive")]
        [property: Description("Copies the packaged output into Unreal's archive layout after packaging completes.")]
        private bool archive = true;
    }
}
