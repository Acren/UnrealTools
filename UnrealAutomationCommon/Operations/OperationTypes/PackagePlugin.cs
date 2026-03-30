using System;
using System.Collections.Generic;
using System.Linq;
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
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(PluginBuildOptions));
        }

        // Fail early when the selected engine cannot even advertise the requested code platforms.
        public override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            string? requirementsError = base.CheckRequirementsSatisfied(operationParameters);
            if (requirementsError != null)
            {
                return requirementsError;
            }

            string? engineSelectionError = typedParameters.GetSingleEngineSelectionValidationMessage();
            if (engineSelectionError != null)
            {
                return engineSelectionError;
            }

            Engine? engine = GetTargetEngineInstall(typedParameters);
            if (engine == null)
            {
                return null;
            }

            return PluginBuildPlatformValidation.CheckRequirementsSatisfied(typedParameters, engine);
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
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
        protected override void OnProcessEnded(global::LocalAutomation.Runtime.OperationResult result)
        {
            base.OnProcessEnded(result);

            if (result.Outcome != global::LocalAutomation.Core.RunOutcome.Succeeded || Cancelled || _requestedTargetPlatforms.Count == 0)
            {
                return;
            }

            List<string> skippedPlatforms = _requestedTargetPlatforms
                .Where(requestedPlatform => !_builtTargetPlatforms.Contains(requestedPlatform, StringComparer.InvariantCultureIgnoreCase))
                .ToList();

            if (skippedPlatforms.Count == 0)
            {
                return;
            }

            Logger.LogError(
                "Unreal BuildPlugin skipped requested target platform(s): {Platforms}. Requested: {Requested}. Built: {Built}.",
                string.Join(", ", skippedPlatforms),
                string.Join(", ", _requestedTargetPlatforms),
                _builtTargetPlatforms.Count > 0 ? string.Join(", ", _builtTargetPlatforms) : "none");
            result.Outcome = global::LocalAutomation.Core.RunOutcome.Failed;
        }

        protected override string GetOperationName()
        {
            return "Package Plugin";
        }

        // Build the final UAT argument list once so validation and execution inspect the same effective request.
        private Arguments BuildPluginArguments(UnrealOperationParameters operationParameters, PluginBuildOptions pluginBuildOptions)
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

            buildPluginArguments.ApplyCommonUATArguments(operationParameters);
            buildPluginArguments.AddAdditionalArguments(operationParameters);
            return buildPluginArguments;
        }
    }
}
