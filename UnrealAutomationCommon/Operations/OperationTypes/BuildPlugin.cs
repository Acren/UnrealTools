namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : Operation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            //Engine\Build\BatchFiles\RunUAT.bat BuildPlugin -Plugin=[Path to .uplugin file, must be outside engine directory] -Package=[Output directory] -Rocket
            Arguments buildPluginArguments = new Arguments();
            buildPluginArguments.AddAction("BuildPlugin");
            buildPluginArguments.AddPath("Plugin", operationParameters.Plugin.UPluginPath);
            buildPluginArguments.AddPath("Package", GetOutputPath(operationParameters));
            buildPluginArguments.AddFlag("Rocket");
            return new Command(operationParameters.Plugin.PluginDescriptor.GetRunUATPath(), buildPluginArguments);
        }

        protected override bool IsPluginOnlyOperation()
        {
            return true;
        }

        protected override string GetOperationName()
        {
            return "Build Plugin";
        }
    }
}
