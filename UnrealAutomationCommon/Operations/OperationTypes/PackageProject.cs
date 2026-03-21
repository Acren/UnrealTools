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
        protected override global::LocalAutomation.Runtime.Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments arguments = UATArguments.MakeBuildArguments(operationParameters);
            arguments.SetFlag("cook");
            arguments.SetFlag("stage");
            arguments.SetFlag("pak");
            arguments.SetFlag("package");
            //arguments.SetFlag("nocompileeditor");

            // Archive
            if (operationParameters.RequestOptions<PackageOptions>().Archive)
            {
                arguments.SetFlag("archive");
                arguments.SetKeyPath("archivedirectory", GetOutputPath(operationParameters));
            }

            // Set cooker exe
            BuildConfiguration cookerConfiguration = operationParameters.RequestOptions<CookOptions>().CookerConfiguration;
            if (cookerConfiguration != BuildConfiguration.Development)
            {
                string unrealExe = operationParameters.Engine.GetEditorCmdExe(cookerConfiguration);
                arguments.SetKeyPath("unrealexe", unrealExe);
            }

            if (operationParameters.RequestOptions<CookOptions>().WaitForAttach)
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
