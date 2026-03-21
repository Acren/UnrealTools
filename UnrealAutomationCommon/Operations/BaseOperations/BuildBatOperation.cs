using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;
using RuntimeTarget = LocalAutomation.Runtime.OperationTarget;

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    // Centralize the direct Build.bat wiring so all UBT-backed compile operations stay consistent.
    public abstract class BuildBatOperation<T> : CommandProcessOperation<T> where T : RuntimeTarget
    {
        // Validate shared direct-UBT overrides once so every Build.bat-backed operation enforces the same limits.
        public override string CheckRequirementsSatisfied(OperationParameters operationParameters)
        {
            string requirementsError = base.CheckRequirementsSatisfied(operationParameters);
            if (requirementsError != null)
            {
                return requirementsError;
            }

            UbtCompilerOptions buildBatOptions = operationParameters.FindOptions<UbtCompilerOptions>();
            if (buildBatOptions == null)
            {
                return null;
            }

            Engine engine = GetTargetEngineInstall(operationParameters);
            if (engine?.Version == null)
            {
                return null;
            }

            // UE 5.5 and newer have dropped Cpp17 support, so fail early before invoking UBT with an invalid override.
            if (buildBatOptions.CppStandard == UbtCppStandard.Cpp17 && engine.Version >= new EngineVersion(5, 5, 0))
            {
                return $"C++17 is not supported for Unreal Engine {engine.Version.MajorMinorString} or newer";
            }

            return null;
        }

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
