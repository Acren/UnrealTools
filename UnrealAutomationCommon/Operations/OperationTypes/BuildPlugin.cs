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
            buildPluginArguments.SetKeyValue("TargetPlatforms", "Win64"); // Specify Win64 only to avoid Win32 being compiled
            if (operationParameters.RequestOptions<PluginBuildOptions>().StrictIncludes) buildPluginArguments.SetFlag("StrictIncludes");
            buildPluginArguments.AddAdditionalArguments(operationParameters);
            return new Command(GetTarget(operationParameters).EngineInstall.GetRunUATPath(), buildPluginArguments);
        }

        protected override string GetOperationName()
        {
            return "Build Plugin";
        }
    }
}