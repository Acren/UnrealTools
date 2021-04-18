using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations
{
    public abstract class PluginOperation : Operation
    {
        public override bool RequirementsSatisfied(OperationParameters operationParameters)
        {
            if (!(operationParameters.Target is Plugin))
            {
                return false;
            }

            return base.RequirementsSatisfied(operationParameters);
        }

        public Plugin GetPlugin(OperationParameters operationParameters)
        {
            return operationParameters.Target as Plugin;
        }
    }
}
