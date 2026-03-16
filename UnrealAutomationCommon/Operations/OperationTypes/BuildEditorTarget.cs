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
            UbtCompiler compiler = operationParameters.RequestOptions<UbtCompilerOptions>().Compiler;
            string editorModuleName = project.ProjectDescriptor.EditorTargetName;

            // Build.bat forwards these arguments directly to UBT, so a compiler override is reliable here.
            args.SetArgument(editorModuleName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(GetTarget(operationParameters).UProjectPath);

            // Only emit an explicit compiler flag when the user has opted out of the engine default behavior.
            if (compiler != UbtCompiler.Default)
            {
                args.SetKeyValue("Compiler", compiler.ToString());
            }

            args.AddAdditionalArguments(operationParameters);
            return new Command(GetTargetEngineInstall(operationParameters).GetBuildPath(), args);
        }
    }
}
