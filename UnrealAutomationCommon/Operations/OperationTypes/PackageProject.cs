using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class PackageProject : CommandProcessOperation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments arguments = UATArguments.MakeBuildArguments(operationParameters);
            arguments.SetFlag("cook");
            arguments.SetFlag("stage");
            arguments.SetFlag("pak");
            arguments.SetFlag("package");
            arguments.SetFlag("nocompileeditor");
            arguments.SetFlag("archive");
            arguments.SetKeyPath("archivedirectory", GetOutputPath(operationParameters));

            // Set cooker exe
            BuildConfiguration cookerConfiguration = operationParameters.RequestOptions<CookOptions>().CookerConfiguration;
            if (cookerConfiguration != BuildConfiguration.Development)
            {
                string unrealExe = operationParameters.EngineInstall.GetEditorCmdExe(cookerConfiguration);
                arguments.SetKeyPath("unrealexe", unrealExe);
            }

            if (operationParameters.RequestOptions<CookOptions>().WaitForAttach)
            {
                arguments.SetKeyValue("additionalcookeroptions","-waitforattach");
            }

            return new Command(GetTargetEngineInstall(operationParameters).GetRunUATPath(), arguments);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}