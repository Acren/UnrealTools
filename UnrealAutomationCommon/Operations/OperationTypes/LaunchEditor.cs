using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public abstract class LaunchEditor<T> : UnrealProcessOperation<T> where T : global::LocalAutomation.Runtime.OperationTarget, IEngineInstanceProvider
    {
        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
            return new global::LocalAutomation.Runtime.Command(GetTargetEngineInstall(operationParameters).GetEditorExe(operationParameters), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true).ToString());
        }
    }

    public class LaunchProjectEditor : LaunchEditor<Project> { }

    public class LaunchEditor : LaunchEditor<Engine> { }
}
