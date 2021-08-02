using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchEditor : SingleCommandOperation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(EnginePaths.GetEditorExe(GetTarget(operationParameters).GetEngineInstall(), operationParameters), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true));
        }

    }
}
