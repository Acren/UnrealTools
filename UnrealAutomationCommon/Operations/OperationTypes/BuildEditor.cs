using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditor : Operation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(GetProject(operationParameters).GetEngineInstall().GetRunUATPath(), UATArguments.MakeArguments(operationParameters));
        }

        protected override string GetOperationName()
        {
            return "Build Editor";
        }

        public override bool SupportsConfiguration(BuildConfiguration configuration)
        {
            // It seems when UAT calls UBT for this it always passes Development config
            // Disallow other configurations to prevent unexpected results
            return configuration == BuildConfiguration.Development;
        }
    }
}
