using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class PackageProject : CommandProcessOperation<Project>
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
                    typeof(BuildConfigurationOptions),
                    typeof(PackageOptions),
                    typeof(CookOptions)
                });
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            Arguments arguments = UATArguments.MakeBuildArguments(operationParameters);
            arguments.SetFlag("cook");
            arguments.SetFlag("stage");
            arguments.SetFlag("pak");
            arguments.SetFlag("package");
            //arguments.SetFlag("nocompileeditor");

            // Archive
            if (operationParameters.GetOptions<PackageOptions>().Archive)
            {
                arguments.SetFlag("archive");
                arguments.SetKeyPath("archivedirectory", GetOutputPath(operationParameters));
            }

            // Set cooker exe
            BuildConfiguration cookerConfiguration = operationParameters.GetOptions<CookOptions>().CookerConfiguration;
            if (cookerConfiguration != BuildConfiguration.Development)
            {
                string unrealExe = engine.GetEditorCmdExe(cookerConfiguration);
                arguments.SetKeyPath("unrealexe", unrealExe);
            }

            if (operationParameters.GetOptions<CookOptions>().WaitForAttach)
            {
                arguments.SetKeyValue("additionalcookeroptions","-waitforattach");
            }

            return new global::LocalAutomation.Runtime.Command(engine.GetRunUATPath(), arguments.ToString());
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
