using System.Collections.Generic;
using System.Linq;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;
using RuntimeTarget = LocalAutomation.Runtime.OperationTarget;

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    // Centralize the direct Build.bat wiring so all UBT-backed compile operations stay consistent.
    public abstract class BuildBatOperation<T> : CommandProcessOperation<T> where T : RuntimeTarget
    {
        /// <summary>
        /// Direct Build.bat-backed operations always expose configuration and direct-UBT compiler override options.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(AdditionalArgumentsOptions),
                    typeof(BuildConfigurationOptions),
                    typeof(UbtCompilerOptions)
                });
        }

        protected override IEnumerable<global::LocalAutomation.Runtime.ExecutionLock> GetExecutionLocks(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            /* Direct Build.bat flows participate in the shared Unreal build lock so multiple callbacks in the same app do
               not race on UnrealBuildTool's writable rules state. */
            foreach (global::LocalAutomation.Runtime.ExecutionLock executionLock in base.GetExecutionLocks(operationParameters))
            {
                yield return executionLock;
            }

            yield return UnrealExecutionLocks.GlobalBuild;
        }

        // Validate shared direct-UBT overrides once so every Build.bat-backed operation enforces the same limits.
        protected override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            string? engineSelectionError = GetSingleEngineSelectionValidationMessage(operationParameters);
            if (engineSelectionError != null)
            {
                return engineSelectionError;
            }

            UbtCompilerOptions buildBatOptions = operationParameters.GetOptions<UbtCompilerOptions>();

            Engine? engine = GetTargetEngineInstall(operationParameters);
            if (engine?.Version == null)
            {
                return null;
            }

            // UE 5.5 and newer have dropped Cpp17 support, so fail early before invoking UBT with an invalid override.
            EngineVersion engineVersion = engine.Version;
            if (buildBatOptions.CppStandard == UbtCppStandard.Cpp17 && engineVersion >= new EngineVersion(5, 5, 0))
            {
                return $"C++17 is not supported for Unreal Engine {engineVersion.MajorMinorString} or newer";
            }

            return null;
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            Arguments args = new();

            // Let derived operations describe the target-specific portion of the Build.bat invocation first.
            ConfigureBuildArguments(operationParameters, args);
            ApplySharedBuildArguments(operationParameters, args);
            return new global::LocalAutomation.Runtime.Command(GetRequiredTargetEngineInstall(operationParameters).GetBuildPath(), args.ToString());
        }

        // Derived operations provide the target name, platform/config, project path, and any target-specific flags.
        protected abstract void ConfigureBuildArguments(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, Arguments args);

        // Apply the shared direct-UBT overrides only for Build.bat flows that are known to respect them.
        protected void ApplySharedBuildArguments(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, Arguments args)
        {
            UbtCompilerOptions buildBatOptions = operationParameters.GetOptions<UbtCompilerOptions>();
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
