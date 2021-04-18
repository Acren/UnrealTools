namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class PackageProject : ProjectOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments arguments = UATArguments.MakeArguments(operationParameters);
            arguments.AddFlag("cook");
            arguments.AddFlag("stage");
            arguments.AddFlag("pak");
            arguments.AddFlag("package");
            arguments.AddFlag("nocompileeditor");
            return new Command(GetProject(operationParameters).ProjectDescriptor.GetRunUATPath(), arguments);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
