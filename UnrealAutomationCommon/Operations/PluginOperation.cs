using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public abstract class PluginOperation : Operation
    {
        public override bool SupportsTarget(OperationTarget Target)
        {
            return Target is Plugin;
        }

        public Plugin GetPlugin(OperationParameters operationParameters)
        {
            return operationParameters.Target as Plugin;
        }
    }
}
