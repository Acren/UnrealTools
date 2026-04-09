using System;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    [Operation(SortOrder = 8)]
    public class BuildPlugin : BuildBatOperation<Plugin>
    {
        // Direct Build.bat plugin compilation needs a host project and only applies to code plugins.
        protected override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("BuildPlugin.CheckRequirements");
            activity.SetTag("target.type", operationParameters.Target?.GetType().Name ?? string.Empty);
            string? engineSelectionError = GetSingleEngineSelectionValidationMessage(operationParameters);
            if (engineSelectionError != null)
            {
                activity.SetTag("result", engineSelectionError);
                return engineSelectionError;
            }

            Plugin plugin = GetRequiredTarget(operationParameters);
            activity.SetTag("plugin.path", plugin.PluginPath)
                .SetTag("descriptor.path", plugin.UPluginPath);
            if (plugin.IsBlueprintOnly)
            {
                activity.SetTag("result", "Build Plugin only supports code plugins");
                return "Build Plugin only supports code plugins";
            }

            Project hostProject = plugin.GetHostProjectForDiagnostics();
            activity.SetTag("host_project.path", hostProject.ProjectPath);
            if (!hostProject.IsValid)
            {
                activity.SetTag("result", "Build Plugin requires the plugin to live inside a valid host project");
                return "Build Plugin requires the plugin to live inside a valid host project";
            }

            Engine? engine = hostProject.GetEngineInstanceForDiagnostics();
            activity.SetTag("engine.name", engine?.DisplayName ?? string.Empty);
            if (engine == null)
            {
                activity.SetTag("result", "Build Plugin could not resolve a host project engine install");
                return "Build Plugin could not resolve a host project engine install";
            }

            activity.SetTag("result", "Success");
            return null;
        }

        // Build the plugin against its host project through Build.bat so plugin compilation stays in place.
        protected override void ConfigureBuildArguments(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, Arguments args)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Project hostProject = plugin.GetHostProjectForDiagnostics();
            if (!hostProject.IsValid)
            {
                throw new InvalidOperationException("Build Plugin requires a valid host project before command generation.");
            }

            // Match Unreal's direct plugin build flow: editor target, platform, configuration, host project, then plugin path.
            args.SetArgument(GetRequiredTargetEngineInstall(operationParameters).BaseEditorName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.GetOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(hostProject.UProjectPath);
            args.SetKeyPath("plugin", plugin.UPluginPath);
        }
    }
}
