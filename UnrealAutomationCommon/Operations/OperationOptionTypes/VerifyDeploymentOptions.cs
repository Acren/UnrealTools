using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public partial class VerifyDeploymentOptions : OperationOptions
    {
        public override int SortIndex => 80;

        // Example projects are discovered under a directory root, so the property grid should offer folder browsing.
        [ObservableProperty]
        [property: DisplayName("Example Projects Path")]
        [property: Description("Points to the folder that contains example projects used for deployment verification.")]
        [property: PathEditor(PathEditorKind.Directory, Title = "Select example projects folder")]
        private string exampleProjectsPath = string.Empty;
    }
}
