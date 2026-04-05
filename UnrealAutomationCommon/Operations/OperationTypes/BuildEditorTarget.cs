using System;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditorTarget : BuildBatOperation<Project>
    {
        // Build the project's editor target directly through Build.bat so direct UBT overrides are honored.
        protected override void ConfigureBuildArguments(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, Arguments args)
        {
            Project project = GetRequiredTarget(operationParameters);
            string editorModuleName = project.ProjectDescriptor?.EditorTargetName
                ?? throw new InvalidOperationException("Build Editor Target requires a loaded project descriptor.");

            // Build.bat forwards these arguments directly to UBT, so a compiler override is reliable here.
            args.SetArgument(editorModuleName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.GetOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(project.UProjectPath);
        }
    }
}
