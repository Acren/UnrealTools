using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : BuildBatOperation<Plugin>
    {
        // Direct Build.bat plugin compilation needs a host project and only applies to code plugins.
        public override string CheckRequirementsSatisfied(OperationParameters operationParameters)
        {
            string requirementsError = base.CheckRequirementsSatisfied(operationParameters);
            if (requirementsError != null)
            {
                return requirementsError;
            }

            Plugin plugin = GetTarget(operationParameters);
            if (plugin.IsBlueprintOnly)
            {
                return "Build Plugin only supports code plugins";
            }

            Project hostProject = plugin.HostProject;
            if (hostProject == null || !hostProject.IsValid)
            {
                return "Build Plugin requires the plugin to live inside a valid host project";
            }

            if (hostProject.EngineInstance == null)
            {
                return "Build Plugin could not resolve a host project engine install";
            }

            return null;
        }

        // Build the plugin against its host project through Build.bat so plugin compilation stays in place.
        protected override void ConfigureBuildArguments(OperationParameters operationParameters, Arguments args)
        {
            Plugin plugin = GetTarget(operationParameters);
            Project hostProject = plugin.HostProject;

            // Match Unreal's direct plugin build flow: editor target, platform, configuration, host project, then plugin path.
            args.SetArgument(GetTargetEngineInstall(operationParameters).BaseEditorName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(hostProject.UProjectPath);
            args.SetKeyPath("plugin", plugin.UPluginPath);
        }
    }
}
