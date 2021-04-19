using System;
using System.Collections.Generic;
using System.Text;

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
    }
}
