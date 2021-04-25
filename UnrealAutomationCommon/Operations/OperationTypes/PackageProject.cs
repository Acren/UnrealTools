namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class PackageProject : ProjectOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments arguments = UATArguments.MakeArguments(operationParameters);
            arguments.SetFlag("cook");
            arguments.SetFlag("stage");
            arguments.SetFlag("pak");
            arguments.SetFlag("package");
            arguments.SetFlag("nocompileeditor");
            return new Command(GetProject(operationParameters).ProjectDescriptor.GetRunUATPath(), arguments);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
