using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : CommandProcessOperation<Plugin>
    {
        private List<string> _requestedTargetPlatforms = new();
        private List<string> _builtTargetPlatforms = new();

        // Fail early when the selected engine cannot even advertise the requested code platforms.
        public override string CheckRequirementsSatisfied(OperationParameters operationParameters)
        {
            string requirementsError = base.CheckRequirementsSatisfied(operationParameters);
            if (requirementsError != null)
            {
                return requirementsError;
            }

            Engine engine = GetTargetEngineInstall(operationParameters);
            if (engine == null)
            {
                return null;
            }

            // Skip platform validation until the option set exists so the UI can still surface the controls
            // that let the user fix an unsupported default selection.
            PluginBuildOptions pluginBuildOptions = operationParameters.FindOptions<PluginBuildOptions>();
            if (pluginBuildOptions == null)
            {
                return null;
            }

            List<string> requestedPlatforms = GetRequestedTargetPlatforms(BuildPluginArguments(operationParameters, pluginBuildOptions));
            if (requestedPlatforms.Count == 0)
            {
                return null;
            }

            List<string> unavailablePlatforms = requestedPlatforms.Where(platform => !IsTargetPlatformAvailable(engine, platform)).ToList();
            if (unavailablePlatforms.Count == 0)
            {
                return null;
            }

            string missingRequirements = string.Join(", ", unavailablePlatforms.SelectMany(platform => GetRequiredTargetFiles(engine, platform)).Distinct());
            string missingDetails = string.IsNullOrEmpty(missingRequirements) ? "The engine does not advertise support for the requested platform." : $"Missing required engine files: {missingRequirements}.";
            return $"Requested target platform(s) {string.Join(", ", unavailablePlatforms)} are not available in engine '{engine.DisplayName}'. Unreal BuildPlugin would silently skip them. {missingDetails}";
        }

        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            //Engine\Build\BatchFiles\RunUAT.bat BuildPlugin -Plugin=[Path to .uplugin file, must be outside engine directory] -Package=[Output directory] -Rocket
            PluginBuildOptions pluginBuildOptions = operationParameters.RequestOptions<PluginBuildOptions>();
            Arguments buildPluginArguments = BuildPluginArguments(operationParameters, pluginBuildOptions);
            _requestedTargetPlatforms = GetRequestedTargetPlatforms(buildPluginArguments);
            _builtTargetPlatforms = new List<string>();
            return new Command(GetTargetEngineInstall(operationParameters).GetRunUATPath(), buildPluginArguments);
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
        protected override void OnProcessEnded(OperationResult result)
        {
            base.OnProcessEnded(result);

            if (!result.Success || Cancelled || _requestedTargetPlatforms.Count == 0)
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
            result.Success = false;
        }

        protected override string GetOperationName()
        {
            return "Build Plugin";
        }

        // Build the final UAT argument list once so validation and execution inspect the same effective request.
        private Arguments BuildPluginArguments(OperationParameters operationParameters, PluginBuildOptions pluginBuildOptions)
        {
            Arguments buildPluginArguments = new();
            buildPluginArguments.SetArgument("BuildPlugin");
            buildPluginArguments.SetKeyPath("Plugin", GetTarget(operationParameters).UPluginPath);
            buildPluginArguments.SetKeyPath("Package", GetOutputPath(operationParameters));
            buildPluginArguments.SetFlag("Rocket");
            buildPluginArguments.SetFlag("VS2019");

            List<string> selectedPlatforms = GetSelectedTargetPlatforms(pluginBuildOptions);
            buildPluginArguments.SetKeyValue("TargetPlatforms", string.Join('+', selectedPlatforms));

            if (pluginBuildOptions.StrictIncludes)
            {
                buildPluginArguments.SetFlag("StrictIncludes");
            }

            buildPluginArguments.ApplyCommonUATArguments(operationParameters);
            buildPluginArguments.AddAdditionalArguments(operationParameters);
            return buildPluginArguments;
        }

        // Keep the current UI options, but convert them into a generic target-platform list for validation.
        private static List<string> GetSelectedTargetPlatforms(PluginBuildOptions pluginBuildOptions)
        {
            bool buildWin64 = pluginBuildOptions.BuildWin64;
            bool buildLinux = pluginBuildOptions.BuildLinux;
            if (!buildWin64 && !buildLinux)
            {
                // If nothing is selected, specify Win64 only to avoid Win32 being compiled.
                buildWin64 = true;
            }

            List<string> selectedPlatforms = new();
            if (buildWin64)
            {
                selectedPlatforms.Add("Win64");
            }

            if (buildLinux)
            {
                selectedPlatforms.Add("Linux");
            }

            return selectedPlatforms;
        }

        // Resolve the effective TargetPlatforms after user overrides so checks match the actual command line.
        private static List<string> GetRequestedTargetPlatforms(Arguments arguments)
        {
            Argument targetPlatformsArgument = arguments.GetArgument("TargetPlatforms");
            if (targetPlatformsArgument == null || string.IsNullOrWhiteSpace(targetPlatformsArgument.Value))
            {
                return new List<string>();
            }

            return targetPlatformsArgument.Value
                .Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(platform => platform.Trim())
                .Where(platform => !string.IsNullOrWhiteSpace(platform))
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .ToList();
        }

        // Installed engines advertise code-platform support through required target files, so check those generically.
        private static bool IsTargetPlatformAvailable(Engine engine, string platform)
        {
            List<string> requiredFiles = GetRequiredTargetFiles(engine, platform);
            if (requiredFiles.Count == 0)
            {
                return true;
            }

            return requiredFiles.All(File.Exists);
        }

        // Mirror Unreal's installed-engine gate by checking the per-platform UnrealGame target files Unreal expects.
        private static List<string> GetRequiredTargetFiles(Engine engine, string platform)
        {
            if (engine.IsSourceBuild)
            {
                return new List<string>();
            }

            return new List<string>
            {
                Path.Combine(engine.TargetPath, "Engine", "Binaries", platform, "UnrealGame.target"),
                Path.Combine(engine.TargetPath, "Engine", "Binaries", platform, $"UnrealGame-{platform}-Shipping.target")
            };
        }
    }
}
