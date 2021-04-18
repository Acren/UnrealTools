namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : PluginOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            //Engine\Build\BatchFiles\RunUAT.bat BuildPlugin -Plugin=[Path to .uplugin file, must be outside engine directory] -Package=[Output directory] -Rocket
            Arguments buildPluginArguments = new Arguments();
            buildPluginArguments.AddArgument("BuildPlugin");
            buildPluginArguments.AddKeyPath("Plugin", GetPlugin(operationParameters).UPluginPath);
            buildPluginArguments.AddKeyPath("Package", GetOutputPath(operationParameters));
            buildPluginArguments.AddFlag("Rocket");
            return new Command(GetPlugin(operationParameters).PluginDescriptor.GetRunUATPath(), buildPluginArguments);
        }

        protected override string GetOperationName()
        {
            return "Build Plugin";
        }
    }
}
