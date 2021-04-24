namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchPackage : ProjectOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(GetProject(operationParameters).GetStagedPackageExecutablePath(), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters)));
        }

        protected override string GetOperationName()
        {
            return "Launch Package";
        }
    }
}
