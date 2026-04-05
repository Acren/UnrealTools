using System.Collections.Generic;
using System.Linq;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditor : CommandProcessOperation<Project>
    {
        /// <summary>
        /// BuildCookRun editor builds always expose build configuration selection.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(OperationOptionTypes.BuildConfigurationOptions),
                    typeof(OperationOptionTypes.PackageOptions)
                });
        }

        protected override IEnumerable<global::LocalAutomation.Runtime.ExecutionLock> GetExecutionLocks(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            /* BuildCookRun editor builds still run through UAT/UBT under the hood, so they share the same process-local
               Unreal build lock as direct Build.bat flows. */
            foreach (global::LocalAutomation.Runtime.ExecutionLock executionLock in base.GetExecutionLocks(operationParameters))
            {
                yield return executionLock;
            }

            yield return UnrealExecutionLocks.GlobalBuild;
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            Arguments args = UATArguments.MakeBuildArguments(operationParameters, engine);
            return new global::LocalAutomation.Runtime.Command(engine.GetRunUATPath(), args.ToString());
        }

        protected override string GetOperationName()
        {
            return "Build Editor";
        }

        /// <summary>
        /// Keeps the BuildCookRun editor flow constrained to Development because UAT forces that configuration
        /// internally even when callers request something else.
        /// </summary>
        private static bool SupportsRequestedConfiguration(BuildConfiguration configuration)
        {
            return configuration == BuildConfiguration.Development;
        }

        /// <summary>
        /// Rejects unsupported build configurations before command generation so the user sees a clear validation
        /// message instead of a misleading UAT invocation.
        /// </summary>
        protected override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            BuildConfiguration configuration = operationParameters.GetOptions<OperationOptionTypes.BuildConfigurationOptions>().Configuration;
            if (!SupportsRequestedConfiguration(configuration))
            {
                return "Configuration is not supported";
            }

            return null;
        }
    }
}
