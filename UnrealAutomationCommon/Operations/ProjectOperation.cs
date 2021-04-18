using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations
{
    public abstract class ProjectOperation : Operation
    {
        public override bool RequirementsSatisfied(OperationParameters operationParameters)
        {
            if (!(operationParameters.Target is Project))
            {
                return false;
            }

            return base.RequirementsSatisfied(operationParameters);
        }

        public Project GetProject(OperationParameters operationParameters)
        {
            return operationParameters.Target as Project;
        }
    }
}
