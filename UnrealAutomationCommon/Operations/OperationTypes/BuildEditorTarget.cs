using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditorTarget : Operation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new Arguments();
            args.SetArgument(GetTargetName(operationParameters) + "Editor");
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(GetProject(operationParameters).UProjectPath);
            return new Command(GetProject(operationParameters).GetEngineInstall().GetBuildPath(), args);
        }
    }
}
