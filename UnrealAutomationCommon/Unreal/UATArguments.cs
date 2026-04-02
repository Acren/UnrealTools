using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    public static class UATArguments
    {
        public static Arguments MakeBuildArguments(ValidatedOperationParameters operationParameters, Engine engine)
        {
            Arguments arguments = new();
            arguments.SetArgument("BuildCookRun");
            if (operationParameters.Target is Project project)
            {
                arguments.SetKeyPath("project", project.UProjectPath);
            }

            arguments.SetFlag("build");
            var configuration = operationParameters.GetOptions<BuildConfigurationOptions>().Configuration.ToString();
            arguments.SetKeyValue("clientconfig", configuration);
            arguments.SetKeyValue("serverconfig", configuration);

            if (operationParameters.GetOptions<PackageOptions>().NoDebugInfo)
            {
                arguments.SetFlag("NoDebugInfo");
            }

            arguments.ApplyCommonUATArguments(engine);

            arguments.AddAdditionalArguments(operationParameters);

            return arguments;
        }

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
