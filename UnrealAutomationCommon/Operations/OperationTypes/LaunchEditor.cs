using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchEditor : CommandProcessOperation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(GetTarget(operationParameters).GetEngineInstall().GetEditorExe(operationParameters), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true));
        }

    }
}
