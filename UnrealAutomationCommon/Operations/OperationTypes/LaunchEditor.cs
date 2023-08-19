using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public abstract class LaunchEditor<T> : UnrealProcessOperation<T> where T : OperationTarget, IEngineInstanceProvider
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(GetTargetEngineInstall(operationParameters).GetEditorExe(operationParameters), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true));
        }
    }

    public class LaunchProjectEditor : LaunchEditor<Project> { }

    public class LaunchEditor : LaunchEditor<Engine> { }
}