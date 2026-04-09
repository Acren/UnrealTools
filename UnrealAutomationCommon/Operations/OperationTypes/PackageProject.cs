using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Extensions.Abstractions;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    [Operation(SortOrder = 6)]
    public class PackageProject : BuildCookRunProjectOperationBase
    {
        /// <summary>
        /// Project packaging always exposes archive and cooker settings because the generated UAT request depends on
        /// both option groups.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(AdditionalArgumentsOptions),
                    typeof(BuildConfigurationOptions),
                    typeof(PackageOptions),
                    typeof(CookOptions)
                });
        }

        /// <summary>
        /// Full project packaging remains a single BuildCookRun command for callers outside Deploy Plugin, but the shared
        /// phase request now makes the build portion explicit so other workflows can split it when needed.
        /// </summary>
        protected override BuildCookRunProjectRequest GetBuildCookRunRequest(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            BuildCookRunProjectPhases phases = BuildCookRunProjectPhases.Cook
                | BuildCookRunProjectPhases.Stage
                | BuildCookRunProjectPhases.Pak
                | BuildCookRunProjectPhases.Package;
            if (operationParameters.GetOptions<PackageOptions>().Build)
            {
                phases |= BuildCookRunProjectPhases.Build;
            }

            return new BuildCookRunProjectRequest(phases, useArchiveOptions: true, useCookOptions: true);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
