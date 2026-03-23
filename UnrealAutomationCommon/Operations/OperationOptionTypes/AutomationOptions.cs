using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class AutomationOptions : OperationOptions
    {
        public override int SortIndex => 50;

        public Option<bool> RunTests { get; } = false;
        public Option<bool> Headless { get; } = true;

        // Persist the effective automation test name with the target repo so test-focused targets travel with their
        // own configuration instead of living only in per-user appdata state.
        [PersistedValue(PersistenceScope.TargetLocal)]
        public Option<string> TestName { get; } = string.Empty;
    }
}
