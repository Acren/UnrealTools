using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    // Centralize the direct Build.bat wiring so all UBT-backed compile operations stay consistent.
    public abstract class BuildBatOperation<T> : CommandProcessOperation<T> where T : OperationTarget
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new();

            // Let derived operations describe the target-specific portion of the Build.bat invocation first.
            ConfigureBuildArguments(operationParameters, args);
            ApplySharedBuildArguments(operationParameters, args);
            return new Command(GetTargetEngineInstall(operationParameters).GetBuildPath(), args);
        }

        // Derived operations provide the target name, platform/config, project path, and any target-specific flags.
        protected abstract void ConfigureBuildArguments(OperationParameters operationParameters, Arguments args);

        // Apply the shared direct-UBT overrides only for Build.bat flows that are known to respect them.
        protected void ApplySharedBuildArguments(OperationParameters operationParameters, Arguments args)
        {
            UbtCompilerOptions buildBatOptions = operationParameters.RequestOptions<UbtCompilerOptions>();
            UbtCompiler compiler = buildBatOptions.Compiler;
            UbtCppStandard cppStandard = buildBatOptions.CppStandard;

            // Only emit an explicit compiler flag when the user has opted out of the engine default behavior.
            if (compiler != UbtCompiler.Default)
            {
                args.SetKeyValue("Compiler", compiler.ToString());
            }

            // Only emit an explicit language standard when the user has selected one of the supported UBT values.
            if (cppStandard != UbtCppStandard.Default)
            {
                args.SetKeyValue("CppStdEngine", cppStandard.ToString());
            }

            args.AddAdditionalArguments(operationParameters);
        }
    }
}
