using LocalAutomation.Core;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : BuildBatOperation<Plugin>
    {
        // Direct Build.bat plugin compilation needs a host project and only applies to code plugins.
        public override string CheckRequirementsSatisfied(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("BuildPlugin.CheckRequirements");
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            string requirementsError = base.CheckRequirementsSatisfied(operationParameters);
            PerformanceTelemetry.SetTag(activity, "target.type", typedParameters.Target?.GetType().Name ?? string.Empty);
            if (requirementsError != null)
            {
                PerformanceTelemetry.SetTag(activity, "result", requirementsError);
                return requirementsError;
            }

            Plugin plugin = GetTarget(typedParameters);
            PerformanceTelemetry.SetTag(activity, "plugin.path", plugin.PluginPath);
            PerformanceTelemetry.SetTag(activity, "descriptor.path", plugin.UPluginPath);
            if (plugin.IsBlueprintOnly)
            {
                PerformanceTelemetry.SetTag(activity, "result", "Build Plugin only supports code plugins");
                return "Build Plugin only supports code plugins";
            }

            Project hostProject = plugin.GetHostProjectForDiagnostics();
            PerformanceTelemetry.SetTag(activity, "host_project.path", hostProject?.ProjectPath ?? string.Empty);
            if (hostProject == null || !hostProject.IsValid)
            {
                PerformanceTelemetry.SetTag(activity, "result", "Build Plugin requires the plugin to live inside a valid host project");
                return "Build Plugin requires the plugin to live inside a valid host project";
            }

            Engine engine = hostProject.GetEngineInstanceForDiagnostics();
            PerformanceTelemetry.SetTag(activity, "engine.name", engine?.DisplayName ?? string.Empty);
            if (engine == null)
            {
                PerformanceTelemetry.SetTag(activity, "result", "Build Plugin could not resolve a host project engine install");
                return "Build Plugin could not resolve a host project engine install";
            }

            PerformanceTelemetry.SetTag(activity, "result", "Success");
            return null;
        }

        // Build the plugin against its host project through Build.bat so plugin compilation stays in place.
        protected override void ConfigureBuildArguments(UnrealOperationParameters operationParameters, Arguments args)
        {
            Plugin plugin = GetTarget(operationParameters);
            Project hostProject = plugin.HostProject;

            // Match Unreal's direct plugin build flow: editor target, platform, configuration, host project, then plugin path.
            args.SetArgument(GetTargetEngineInstall(operationParameters).BaseEditorName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.Value.ToString());
            args.SetPath(hostProject.UProjectPath);
            args.SetKeyPath("plugin", plugin.UPluginPath);
        }
    }
}
