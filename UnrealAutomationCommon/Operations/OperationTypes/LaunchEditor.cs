namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchEditor : Operation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(EnginePaths.GetEditorExe(GetTarget(operationParameters).ProjectDescriptor.GetEngineInstallDirectory(), operationParameters), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true));
        }

    }
}
