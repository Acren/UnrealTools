using System;
using System.Collections.Generic;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class DeployPlugin : Operation<Plugin>
    {
        protected override void OnExecuted(OperationParameters operationParameters, IOperationLogger logger)
        {
            throw new NotImplementedException();
        }

        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            return new List<Command>();
        }
    }
}
