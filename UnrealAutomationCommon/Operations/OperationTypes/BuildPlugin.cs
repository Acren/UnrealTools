using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : CommandProcessOperation<Plugin>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            //Engine\Build\BatchFiles\RunUAT.bat BuildPlugin -Plugin=[Path to .uplugin file, must be outside engine directory] -Package=[Output directory] -Rocket
            Arguments buildPluginArguments = new Arguments();
            buildPluginArguments.SetArgument("BuildPlugin");
            buildPluginArguments.SetKeyPath("Plugin", GetTarget(operationParameters).UPluginPath);
            buildPluginArguments.SetKeyPath("Package", GetOutputPath(operationParameters));
            buildPluginArguments.SetFlag("Rocket");
            buildPluginArguments.SetFlag("VS2019");
            return new Command(GetTarget(operationParameters).PluginDescriptor.GetRunUATPath(), buildPluginArguments);
        }

        protected override string GetOperationName()
        {
            return "Build Plugin";
        }
    }
}
