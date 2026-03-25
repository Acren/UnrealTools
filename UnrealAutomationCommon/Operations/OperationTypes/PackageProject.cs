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
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(BuildConfigurationOptions));
            optionSetTypes.Add(typeof(PackageOptions));
            optionSetTypes.Add(typeof(CookOptions));
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
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
            BuildConfiguration cookerConfiguration = operationParameters.GetOptions<CookOptions>().CookerConfiguration.Value;
            if (cookerConfiguration != BuildConfiguration.Development)
            {
                string unrealExe = operationParameters.Engine.GetEditorCmdExe(cookerConfiguration);
                arguments.SetKeyPath("unrealexe", unrealExe);
            }

            if (operationParameters.GetOptions<CookOptions>().WaitForAttach)
            {
                arguments.SetKeyValue("additionalcookeroptions","-waitforattach");
            }

            return new global::LocalAutomation.Runtime.Command(GetTargetEngineInstall(operationParameters).GetRunUATPath(), arguments.ToString());
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
