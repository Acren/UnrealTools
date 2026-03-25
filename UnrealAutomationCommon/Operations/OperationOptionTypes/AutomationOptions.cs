using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class AutomationOptions : OperationOptions
    {
        public override int SortIndex => 50;

        [ObservableProperty]
        private bool runTests = false;

        [ObservableProperty]
        private bool headless = true;

        // Persist the effective automation test name with the target repo so test-focused targets travel with their
        // own configuration instead of living only in per-user appdata state.
        [ObservableProperty]
        [property: PersistedValue(PersistenceScope.TargetLocal)]
        private string testName = string.Empty;
    }
}
