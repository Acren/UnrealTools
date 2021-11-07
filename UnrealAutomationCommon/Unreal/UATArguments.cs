using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    public static class UATArguments
    {
        public static Arguments MakeBuildArguments(OperationParameters operationParameters)
        {
            Arguments arguments = new();
            arguments.SetArgument("BuildCookRun");
            if (operationParameters.Target is Project project)
            {
                arguments.SetKeyPath("project", project.UProjectPath);
            }

            arguments.SetFlag("build");
            var configuration = operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.ToString();
            arguments.SetKeyValue("clientconfig", configuration);
            arguments.SetKeyValue("serverconfig", configuration);

            if (operationParameters.RequestOptions<PackageOptions>().NoDebugInfo)
            {
                arguments.SetFlag("NoDebugInfo");
            }

            arguments.ApplyCommonUATArguments(operationParameters);

            arguments.AddAdditionalArguments(operationParameters);

            return arguments;
        }

        public static void ApplyCommonUATArguments(this Arguments arguments, OperationParameters operationParameters)
        {
            if ((operationParameters.Target as IEngineInstallProvider)?.EngineInstall.Version >= new EngineInstallVersion(5, 0))
            {
                // Prevent turnkey errors in UE5
                arguments.SetFlag("noturnkeyvariables");
            }
        }
    }
}