using System;
using System.Threading;
using System.Threading.Tasks;
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

            return new Command(GetTargetEngineInstall(operationParameters).GetRunUATPath(), arguments);
        }

        protected override Task<OperationResult> OnExecuted(CancellationToken token)
        {
            Project targetProject = GetTarget(OperationParameters);
            if (targetProject.IsBlueprintOnly && targetProject.Plugins.Count > 0)
            {
                // Note: The specific error this causes is "Plugin X failed to load because module Y could not be found" when launching the package
                throw new Exception("Blueprint-only project contains plugins directly. Although the engine will normally allow this, it will cause errors that are difficult to debug. Install the plugin to the engine instead.");
            }
            
            return base.OnExecuted(token);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}