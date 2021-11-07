using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditor : CommandProcessOperation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = UATArguments.MakeBuildArguments(operationParameters);
            if (args.GetArgument("ubtargs") != null)
            {
                if (Executing)
                {
                    Logger.Log("BuildCookRun editor targets do not respect UbtArgs argument - consider direct UBT usage instead", LogVerbosity.Warning);
                }
            }
            return new Command(GetTarget(operationParameters).EngineInstall.GetRunUATPath(), args);
        }

        protected override string GetOperationName()
        {
            return "Build Editor";
        }

        public override bool SupportsConfiguration(BuildConfiguration configuration)
        {
            // It seems when UAT calls UBT for this it always passes Development config
            // Disallow other configurations to prevent unexpected results
            return configuration == BuildConfiguration.Development;
        }
    }
}