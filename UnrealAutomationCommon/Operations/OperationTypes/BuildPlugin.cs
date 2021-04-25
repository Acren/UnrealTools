namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : PluginOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            //Engine\Build\BatchFiles\RunUAT.bat BuildPlugin -Plugin=[Path to .uplugin file, must be outside engine directory] -Package=[Output directory] -Rocket
            Arguments buildPluginArguments = new Arguments();
            buildPluginArguments.SetArgument("BuildPlugin");
            buildPluginArguments.SetKeyPath("Plugin", GetPlugin(operationParameters).UPluginPath);
            buildPluginArguments.SetKeyPath("Package", GetOutputPath(operationParameters));
            buildPluginArguments.SetFlag("Rocket");
            return new Command(GetPlugin(operationParameters).PluginDescriptor.GetRunUATPath(), buildPluginArguments);
        }

        protected override string GetOperationName()
        {
            return "Build Plugin";
        }
    }
}
