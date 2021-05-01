namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchPackage : ProjectOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters));
            args.SetFlag("windowed");
            args.SetKeyValue("resx", "1920", false);
            args.SetKeyValue("resy", "1080", false);
            return new Command(GetProject(operationParameters).GetStagedPackageExecutablePath(), args);
        }

        protected override string GetOperationName()
        {
            return "Launch Package";
        }

        public override string GetLogsPath(OperationParameters operationParameters)
        {
            return GetProject(operationParameters).GetStagedPackage().GetLogsPath();
        }
    }
}
