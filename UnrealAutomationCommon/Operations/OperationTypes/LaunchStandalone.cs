namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchStandalone : ProjectOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true);
            args.AddFlag("game");
            args.AddFlag("windowed");
            args.AddKeyValue("resx", "1920");
            args.AddKeyValue("resy", "1080");
            return new Command(EnginePaths.GetEditorExe(GetProject(operationParameters).ProjectDescriptor.GetEngineInstallDirectory(), operationParameters), args);
        }
    }
}
