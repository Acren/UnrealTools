using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class PluginDeployOptions : OperationOptions
    {
        public override int SortIndex => 80;

        // Keep the Fab-style Clang validation opt-in because the direct plugin build path is the closest match.
        [ObservableProperty]
        private bool runClangCompileCheck = false;

        [ObservableProperty]
        private bool testStandalone = true;

        [ObservableProperty]
        private bool testPackageWithProjectPlugin = true;

        [ObservableProperty]
        private bool testPackageWithEnginePlugin = true;

        // Archive output is a folder root, so expose a directory picker in hosts that understand path metadata.
        [ObservableProperty]
        [property: PathEditor(PathEditorKind.Directory, Title = "Select archive output folder")]
        [property: PersistedValue(PersistenceScope.Global)]
        private string archivePath = string.Empty;

        [ObservableProperty]
        private bool archivePluginBuild = false;

        [ObservableProperty]
        private bool archiveExampleProject = true;

        [ObservableProperty]
        private bool archiveDemoPackage = true;

        [ObservableProperty]
        private bool includeOtherPlugins = false;

        [ObservableProperty]
        [property: PersistedValue(PersistenceScope.TargetLocal)]
        private string excludePlugins = string.Empty;
    }
}
