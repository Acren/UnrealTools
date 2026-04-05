using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class PluginBuildOptions : OperationOptions
    {
        public override int SortIndex => 45;

        [ObservableProperty]
        [property: DisplayName("Build Win64")]
        [property: Description("Builds the plugin for the Win64 target platform.")]
        private bool buildWin64 = true;

        [ObservableProperty]
        [property: DisplayName("Build Linux")]
        [property: Description("Builds the plugin for the Linux target platform.")]
        private bool buildLinux = true;

        [ObservableProperty]
        [property: DisplayName("Strict Includes")]
        [property: Description("Enables Unreal's stricter include validation during plugin builds.")]
        private bool strictIncludes = true;
    }
}
