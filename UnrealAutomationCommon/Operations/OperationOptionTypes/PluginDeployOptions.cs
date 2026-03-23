using global::LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class PluginDeployOptions : global::LocalAutomation.Runtime.OperationOptions
    {
        public override int SortIndex => 80;

        // Keep the Fab-style Clang validation opt-in because the direct plugin build path is the closest match.
        public global::LocalAutomation.Runtime.Option<bool> RunClangCompileCheck { get; } = false;
        public global::LocalAutomation.Runtime.Option<bool> TestStandalone { get; } = true;
        public global::LocalAutomation.Runtime.Option<bool> TestPackageWithProjectPlugin { get; } = true;
        public global::LocalAutomation.Runtime.Option<bool> TestPackageWithEnginePlugin { get; } = true;
        [PersistedValue(PersistenceScope.Global)]
        public global::LocalAutomation.Runtime.Option<string> ArchivePath { get; } = "";
        public global::LocalAutomation.Runtime.Option<bool> ArchivePluginBuild { get; } = false;
        public global::LocalAutomation.Runtime.Option<bool> ArchiveExampleProject { get; } = true;
        public global::LocalAutomation.Runtime.Option<bool> ArchiveDemoPackage { get; } = true;
        public global::LocalAutomation.Runtime.Option<bool> IncludeOtherPlugins { get; } = false;
        [PersistedValue(PersistenceScope.TargetLocal)]
        public global::LocalAutomation.Runtime.Option<string> ExcludePlugins { get; } = "";
    }
}
