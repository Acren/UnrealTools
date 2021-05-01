using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public abstract class ProjectOperation : Operation
    {
        public override bool SupportsTarget(OperationTarget Target)
        {
            return Target is Project;
        }

        public Project GetProject(OperationParameters operationParameters)
        {
            return operationParameters.Target as Project;
        }

        public override string GetLogsPath(OperationParameters operationParameters)
        {
            return GetProject(operationParameters).GetLogsPath();
        }
    }
}
