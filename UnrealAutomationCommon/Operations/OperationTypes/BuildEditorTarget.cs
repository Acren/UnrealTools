using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditorTarget : CommandProcessOperation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new();
            args.SetArgument(GetTargetName(operationParameters) + "Editor");
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(GetTarget(operationParameters).UProjectPath);
            return new Command(GetTarget(operationParameters).EngineInstall.GetBuildPath(), args);
        }
    }
}