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
            Project project = GetTarget(operationParameters);
            string editorModuleName = project.ProjectDescriptor.EditorTargetName;
            args.SetArgument(editorModuleName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(GetTarget(operationParameters).UProjectPath);
            args.AddAdditionalArguments(operationParameters);
            return new Command(GetTargetEngineInstall(operationParameters).GetBuildPath(), args);
        }
    }
}