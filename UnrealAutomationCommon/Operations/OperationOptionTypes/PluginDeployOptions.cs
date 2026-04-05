using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class PluginDeployOptions : OperationOptions
    {
        public override int SortIndex => 80;

        // Keep the Fab-style Clang validation opt-in because the direct plugin build path is the closest match.
        [ObservableProperty]
        [property: DisplayName("Run Clang Compile Check")]
        [property: Description("Runs the Clang-based compile validation step as part of deployment verification.")]
        private bool runClangCompileCheck = false;

        [ObservableProperty]
        [property: DisplayName("Test Standalone")]
        [property: Description("Builds and validates the standalone packaged plugin output.")]
        private bool testStandalone = true;

        [ObservableProperty]
        [property: DisplayName("Test Package With Project Plugin")]
        [property: Description("Validates packaging a sample project with the plugin installed at the project level.")]
        private bool testPackageWithProjectPlugin = true;

        [ObservableProperty]
        [property: DisplayName("Test Package With Engine Plugin")]
        [property: Description("Validates packaging a sample project with the plugin installed in the engine.")]
        private bool testPackageWithEnginePlugin = true;

        // Archive output is a folder root, so expose a directory picker in hosts that understand path metadata.
        [ObservableProperty]
        [property: DisplayName("Archive Path")]
        [property: Description("Chooses the root folder used for deployment archives and exported artifacts.")]
        [property: PathEditor(PathEditorKind.Directory, Title = "Select archive output folder")]
        [property: PersistedValue(PersistenceScope.Global)]
        private string archivePath = string.Empty;

        [ObservableProperty]
        [property: DisplayName("Archive Plugin Build")]
        [property: Description("Archives the built plugin output after deployment validation completes.")]
        private bool archivePluginBuild = false;

        [ObservableProperty]
        [property: DisplayName("Archive Example Project")]
        [property: Description("Archives the prepared example project used during deployment verification.")]
        private bool archiveExampleProject = true;

        [ObservableProperty]
        [property: DisplayName("Archive Demo Package")]
        [property: Description("Archives the packaged demo output produced during deployment verification.")]
        private bool archiveDemoPackage = true;

        [ObservableProperty]
        [property: DisplayName("Include Other Plugins")]
        [property: Description("Includes sibling plugins when assembling deployment verification inputs and archives.")]
        private bool includeOtherPlugins = false;

        [ObservableProperty]
        [property: DisplayName("Exclude Plugins")]
        [property: Description("Provides a delimited list of plugin names to omit from deployment verification inputs.")]
        [property: PersistedValue(PersistenceScope.TargetLocal)]
        private string excludePlugins = string.Empty;
    }
}
