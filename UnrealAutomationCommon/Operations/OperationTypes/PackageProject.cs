namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class PackageProject : Operation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments arguments = UATArguments.MakeArguments(operationParameters);
            arguments.AddFlag("cook");
            arguments.AddFlag("stage");
            arguments.AddFlag("pak");
            return new Command(operationParameters.Project.ProjectDescriptor.GetRunUATPath(), arguments);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
