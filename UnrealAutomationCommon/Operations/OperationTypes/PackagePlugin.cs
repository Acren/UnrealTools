using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class PackagePlugin : CommandProcessOperation<Plugin>
    {
        private List<string> _requestedTargetPlatforms = new();
        private List<string> _builtTargetPlatforms = new();

        /// <summary>
        /// Packaging a plugin always exposes the plugin platform selection options used to build the final UAT request.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[] { typeof(AdditionalArgumentsOptions), typeof(PluginBuildOptions) });
        }

        // Fail early when the selected engine cannot even advertise the requested code platforms.
        protected override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            string? engineSelectionError = GetSingleEngineSelectionValidationMessage(operationParameters);
            if (engineSelectionError != null)
            {
                return engineSelectionError;
            }

            Engine? engine = GetTargetEngineInstall(operationParameters);
            if (engine == null)
            {
                return null;
            }

            return PluginBuildPlatformValidation.CheckRequirementsSatisfied(operationParameters, engine);
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            // Package the plugin into a distributable output folder through UAT's BuildPlugin flow.
            PluginBuildOptions pluginBuildOptions = operationParameters.GetOptions<PluginBuildOptions>();
            Arguments buildPluginArguments = BuildPluginArguments(operationParameters, pluginBuildOptions);
            _requestedTargetPlatforms = PluginBuildPlatformValidation.GetRequestedTargetPlatforms(buildPluginArguments);
            _builtTargetPlatforms = new List<string>();
            return new global::LocalAutomation.Runtime.Command(GetRequiredTargetEngineInstall(operationParameters).GetRunUATPath(), buildPluginArguments.ToString());
        }

        // Track Unreal's reported target platform list as it streams by so we do not need to retain the full log.
        protected override void OnOutputLine(string line)
        {
            base.OnOutputLine(line);

            const string prefix = "Building plugin for target platforms:";
            int prefixIndex = line.IndexOf(prefix, StringComparison.InvariantCultureIgnoreCase);
            if (prefixIndex < 0)
            {
                return;
            }

            string builtPlatformsValue = line.Substring(prefixIndex + prefix.Length).Trim();
            _builtTargetPlatforms = string.IsNullOrWhiteSpace(builtPlatformsValue)
                ? new List<string>()
                : builtPlatformsValue
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(platform => platform.Trim())
                    .Where(platform => !string.IsNullOrWhiteSpace(platform))
                    .Distinct(StringComparer.InvariantCultureIgnoreCase)
                    .ToList();
        }

        // Compare Unreal's reported target platform list with what the user requested so silent skips become failures.
        protected override void OnProcessEnded(global::LocalAutomation.Runtime.ExecutionTaskContext context, global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.OperationResult result)
        {
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("PackagePlugin.OnProcessEnded")
                .SetTag("result.outcome", result.Outcome.ToString())
                .SetTag("result.was_cancelled", result.WasCancelled)
                .SetTag("requested_platform.count", _requestedTargetPlatforms.Count);

            base.OnProcessEnded(context, operationParameters, result);

            if (result.Outcome != global::LocalAutomation.Runtime.RunOutcome.Succeeded || result.WasCancelled || _requestedTargetPlatforms.Count == 0)
            {
                activity.SetTag("validation.skipped", true);
                return;
            }

            List<string> skippedPlatforms = _requestedTargetPlatforms
                .Where(requestedPlatform => !_builtTargetPlatforms.Contains(requestedPlatform, StringComparer.InvariantCultureIgnoreCase))
                .ToList();
            activity.SetTag("built_platform.count", _builtTargetPlatforms.Count)
                .SetTag("skipped_platform.count", skippedPlatforms.Count);

            if (skippedPlatforms.Count == 0)
            {
                return;
            }

            context.Logger.LogError(
                "Unreal BuildPlugin skipped requested target platform(s): {Platforms}. Requested: {Requested}. Built: {Built}.",
                string.Join(", ", skippedPlatforms),
                string.Join(", ", _requestedTargetPlatforms),
                _builtTargetPlatforms.Count > 0 ? string.Join(", ", _builtTargetPlatforms) : "none");
            result.Outcome = global::LocalAutomation.Runtime.RunOutcome.Failed;
        }

        protected override string GetOperationName()
        {
            return "Package Plugin";
        }

        // Build the final UAT argument list once so validation and execution inspect the same effective request.
        private Arguments BuildPluginArguments(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, PluginBuildOptions pluginBuildOptions)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Arguments buildPluginArguments = new();
            buildPluginArguments.SetArgument("BuildPlugin");
            buildPluginArguments.SetKeyPath("Plugin", plugin.UPluginPath);
            buildPluginArguments.SetKeyPath("Package", GetOutputPath(operationParameters));
            buildPluginArguments.SetFlag("Rocket");

            List<string> selectedPlatforms = PluginBuildPlatformValidation.GetSelectedTargetPlatforms(pluginBuildOptions);
            buildPluginArguments.SetKeyValue("TargetPlatforms", string.Join('+', selectedPlatforms));

            if (pluginBuildOptions.StrictIncludes)
            {
                buildPluginArguments.SetFlag("StrictIncludes");
            }

            buildPluginArguments.ApplyCommonUATArguments(GetRequiredTargetEngineInstall(operationParameters));
            buildPluginArguments.AddAdditionalArguments(operationParameters);
            return buildPluginArguments;
        }
    }
}
