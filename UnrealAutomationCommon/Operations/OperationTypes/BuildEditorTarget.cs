using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditorTarget : BuildBatOperation<Project>
    {
        // Build the project's editor target directly through Build.bat so direct UBT overrides are honored.
        protected override void ConfigureBuildArguments(UnrealOperationParameters operationParameters, Arguments args)
        {
            Project project = GetTarget(operationParameters);
            string editorModuleName = project.ProjectDescriptor.EditorTargetName;

            // Build.bat forwards these arguments directly to UBT, so a compiler override is reliable here.
            args.SetArgument(editorModuleName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.Value.ToString());
            args.SetPath(GetTarget(operationParameters).UProjectPath);
        }
    }
}
