using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    public static class UnrealArgumentUtils
    {
        public static void AddAdditionalArguments(this Arguments arguments, OperationParameters operationParameters)
        {
            if (!string.IsNullOrWhiteSpace(operationParameters.AdditionalArguments))
            {
                arguments.AddRawArgsString(operationParameters.AdditionalArguments);
            }
        }
    }
}
