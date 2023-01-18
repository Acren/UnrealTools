namespace UnrealAutomationCommon.Operations.OperationOptionTypes
{
    public class VerifyDeploymentOptions : OperationOptions
    {
        public override int SortIndex => 80;

        public Option<string> ExampleProjectsPath { get; } = "";
    }
}
