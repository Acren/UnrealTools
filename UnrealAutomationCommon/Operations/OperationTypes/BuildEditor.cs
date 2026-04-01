using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditor : CommandProcessOperation<Project>
    {
        /// <summary>
        /// BuildCookRun editor builds always expose build configuration selection.
        /// </summary>
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(OperationOptionTypes.BuildConfigurationOptions));
            optionSetTypes.Add(typeof(OperationOptionTypes.PackageOptions));
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
            Arguments args = UATArguments.MakeBuildArguments(operationParameters);
            return new global::LocalAutomation.Runtime.Command(GetRequiredTargetEngineInstall(operationParameters).GetRunUATPath(), args.ToString());
        }

        protected override string GetOperationName()
        {
            return "Build Editor";
        }

        /// <summary>
        /// Keeps the BuildCookRun editor flow constrained to Development because UAT forces that configuration
        /// internally even when callers request something else.
        /// </summary>
        private static bool SupportsRequestedConfiguration(BuildConfiguration configuration)
        {
            return configuration == BuildConfiguration.Development;
        }

        /// <summary>
        /// Rejects unsupported build configurations before command generation so the user sees a clear validation
        /// message instead of a misleading UAT invocation.
        /// </summary>
        public override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            string? baseError = base.CheckRequirementsSatisfied(operationParameters);
            if (baseError != null)
            {
                return baseError;
            }

            BuildConfiguration configuration = typedParameters.GetOptions<OperationOptionTypes.BuildConfigurationOptions>().Configuration;
            if (!SupportsRequestedConfiguration(configuration))
            {
                return "Configuration is not supported";
            }

            return null;
        }
    }
}
