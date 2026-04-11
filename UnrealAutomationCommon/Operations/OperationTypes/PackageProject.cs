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
        /// request now carries the exact BuildCookRun command settings directly so the base class does not need to read
        /// package or cook option models.
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

            PackageOptions packageOptions = operationParameters.GetOptions<PackageOptions>();
            CookOptions cookOptions = operationParameters.GetOptions<CookOptions>();
            BuildConfiguration cookerConfiguration = cookOptions.CookerConfiguration;
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);

            /* Package Project still exposes the user-facing package and cook option sets, but it now translates those
               values into one self-contained BuildCookRun request before the shared base assembles UAT arguments. */
            return new BuildCookRunProjectRequest(
                phases,
                configuration: operationParameters.GetOptions<BuildConfigurationOptions>().Configuration,
                noDebugInfo: packageOptions.NoDebugInfo,
                archiveDirectory: packageOptions.Archive ? GetOutputPath(operationParameters) : null,
                unrealExePath: cookerConfiguration != BuildConfiguration.Development ? engine.GetEditorCmdExe(cookerConfiguration) : null,
                additionalCookerOptions: cookOptions.WaitForAttach ? "-waitforattach" : null);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
