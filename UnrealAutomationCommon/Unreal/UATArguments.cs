using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    public static class UATArguments
    {
        public static Arguments MakeArguments(OperationParameters operationParameters)
        {
            Arguments arguments = new Arguments();
            arguments.SetArgument("BuildCookRun");
            if (operationParameters.Target is Project project)
            {
                arguments.SetKeyPath("project", project.UProjectPath);
            }
            arguments.SetFlag("build");
            string configuration = operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.ToString();
            arguments.SetKeyValue("clientconfig", configuration);
            arguments.SetKeyValue("serverconfig", configuration);

            if(operationParameters.RequestOptions<PackageOptions>().NoDebugInfo)
            {
                arguments.SetFlag("NoDebugInfo");
            }

            if (!string.IsNullOrWhiteSpace(operationParameters.AdditionalArguments))
            {
                arguments.AddRawArgsString(operationParameters.AdditionalArguments);
            }

            return arguments;
        }
    }
}
