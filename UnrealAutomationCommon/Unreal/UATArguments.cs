using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    public static class UATArguments
    {
        public static void ApplyCommonUATArguments(this Arguments arguments, Engine engine)
        {
            if (engine.Version != null && engine.Version >= new EngineVersion(5, 0))
            {
                // Prevent turnkey errors in UE5
                arguments.SetFlag("noturnkeyvariables");
            }
        }
    }
}
