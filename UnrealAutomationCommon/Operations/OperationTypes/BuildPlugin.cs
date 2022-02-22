using System.Collections.Generic;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : CommandProcessOperation<Plugin>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            //Engine\Build\BatchFiles\RunUAT.bat BuildPlugin -Plugin=[Path to .uplugin file, must be outside engine directory] -Package=[Output directory] -Rocket
            Arguments buildPluginArguments = new();
            buildPluginArguments.SetArgument("BuildPlugin");
            buildPluginArguments.SetKeyPath("Plugin", GetTarget(operationParameters).UPluginPath);
            buildPluginArguments.SetKeyPath("Package", GetOutputPath(operationParameters));
            buildPluginArguments.SetFlag("Rocket");
            buildPluginArguments.SetFlag("VS2019");

            bool buildWin64 = operationParameters.RequestOptions<PluginBuildOptions>().BuildWin64;
            bool buildLinux = operationParameters.RequestOptions<PluginBuildOptions>().BuildLinux;
            if (!buildWin64 && !buildLinux)
            {
                // If nothing is selected, specify Win64 only to avoid Win32 being compiled
                buildWin64 = true;
            }

            List<string> platformsValue = new List<string>();
            if(buildWin64)
            {
                platformsValue.Add("Win64");
            }

            if(buildLinux)
            {
                platformsValue.Add("Linux");
            }

            buildPluginArguments.SetKeyValue("TargetPlatforms", string.Join('+', platformsValue));

            if (operationParameters.RequestOptions<PluginBuildOptions>().StrictIncludes)
            {
                buildPluginArguments.SetFlag("StrictIncludes");
            }

            buildPluginArguments.ApplyCommonUATArguments(operationParameters);

            buildPluginArguments.AddAdditionalArguments(operationParameters);
            return new Command(GetTargetEngineInstall(operationParameters).GetRunUATPath(), buildPluginArguments);
        }

        protected override string GetOperationName()
        {
            return "Build Plugin";
        }
    }
}