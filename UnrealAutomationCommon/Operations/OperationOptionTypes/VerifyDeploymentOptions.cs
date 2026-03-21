using global::LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class VerifyDeploymentOptions : global::LocalAutomation.Runtime.OperationOptions
    {
        public override int SortIndex => 80;

        public global::LocalAutomation.Runtime.Option<string> ExampleProjectsPath { get; } = "";
    }
}
