namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchEditor : ProjectOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(EnginePaths.GetEditorExe(GetProject(operationParameters).ProjectDescriptor.GetEngineInstallDirectory(), operationParameters), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true));
        }

    }
}
