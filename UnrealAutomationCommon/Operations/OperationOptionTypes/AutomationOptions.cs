using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class AutomationOptions : OperationOptions
    {
        public override int SortIndex => 50;

        [ObservableProperty]
        [property: DisplayName("Run Tests")]
        [property: Description("Runs Unreal automation tests as part of the launched editor session.")]
        private bool runTests = false;

        [ObservableProperty]
        [property: DisplayName("Headless")]
        [property: Description("Runs automation tests unattended with null RHI instead of opening a visible editor window.")]
        private bool headless = true;

        // Persist the effective automation test filter with the target repo so test-focused targets travel with their
        // own configuration instead of living only in per-user appdata state.
        [ObservableProperty]
        [property: DisplayName("Test Filter")]
        [property: Description("Supplies the filter passed to Unreal's Automation RunTests command.")]
        [property: PersistedValue(PersistenceScope.TargetLocal)]
        private string testFilter = string.Empty;
    }
}
