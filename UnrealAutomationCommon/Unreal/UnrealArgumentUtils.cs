using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    public static class UnrealArgumentUtils
    {
        public static void AddAdditionalArguments(this Arguments arguments, ValidatedOperationParameters operationParameters)
        {
            if (!operationParameters.TryGetOptions<AdditionalArgumentsOptions>(out AdditionalArgumentsOptions? additionalArgumentsOptions))
            {
                return;
            }

            if (additionalArgumentsOptions == null)
            {
                return;
            }

            string additionalArguments = additionalArgumentsOptions.Arguments;
            if (!string.IsNullOrWhiteSpace(additionalArguments))
            {
                arguments.AddRawArgsString(additionalArguments);
            }
        }
    }
}
