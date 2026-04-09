using System.Collections.Generic;
using System.Linq;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditor : BuildCookRunProjectOperationBase
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

        /// <summary>
        /// Editor prebuilds are modeled as the build-only BuildCookRun preset used by packaging workflows to prepare code
        /// outputs without entering cook or package phases.
        /// </summary>
        protected override BuildCookRunProjectRequest GetBuildCookRunRequest(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            return new BuildCookRunProjectRequest(BuildCookRunProjectPhases.Build);
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
