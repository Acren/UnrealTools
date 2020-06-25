namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditor : Operation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(operationParameters.Project.ProjectDescriptor.GetRunUATPath(), UATArguments.MakeArguments(operationParameters) );
        }

        protected override string GetOperationName()
        {
            return "Build Editor";
        }
    }
}
