using global::LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class AutomationOptions : global::LocalAutomation.Runtime.OperationOptions
    {
        public override int SortIndex => 50;

        public global::LocalAutomation.Runtime.Option<bool> RunTests { get; } = false;
        public global::LocalAutomation.Runtime.Option<bool> Headless { get; } = true;

        // Persist the effective automation test name with the target repo so test-focused targets travel with their
        // own configuration instead of living only in per-user appdata state.
        [PersistedValue(PersistenceScope.TargetLocal)]
        public global::LocalAutomation.Runtime.Option<string> TestName { get; } = string.Empty;
    }
}
