using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public static class PluginBuildPlatformValidation
    {
        // Validate the effective requested plugin target platforms against the selected engine.
        public static string? CheckRequirementsSatisfied(ValidatedOperationParameters operationParameters, Engine engine)
        {
            PluginBuildOptions pluginBuildOptions = operationParameters.GetOptions<PluginBuildOptions>();

            List<string> requestedPlatforms = GetRequestedTargetPlatforms(operationParameters, pluginBuildOptions);
            if (requestedPlatforms.Count == 0)
            {
                return null;
            }

            List<string> unavailablePlatforms = requestedPlatforms.Where(platform => !IsTargetPlatformAvailable(engine, platform)).ToList();
            if (unavailablePlatforms.Count == 0)
            {
                return null;
            }

            string unavailablePlatformList = string.Join(", ", unavailablePlatforms);
            if (engine.IsSourceBuild)
            {
                return $"{unavailablePlatformList} is not available in '{engine.DisplayName}'. Build or install support for that platform, or disable it before running BuildPlugin.";
            }

            return $"{unavailablePlatformList} is not installed for '{engine.DisplayName}'. Install platform support in Epic Games Launcher, or disable it before running BuildPlugin.";
        }

        // Resolve the final TargetPlatforms value after AdditionalArguments overrides so validation matches execution.
        public static List<string> GetRequestedTargetPlatforms(ValidatedOperationParameters operationParameters, PluginBuildOptions pluginBuildOptions)
        {
            Arguments arguments = new();
            arguments.SetKeyValue("TargetPlatforms", string.Join('+', GetSelectedTargetPlatforms(pluginBuildOptions)));
            arguments.AddAdditionalArguments(operationParameters);
            return GetRequestedTargetPlatforms(arguments);
        }

        // Parse a built argument list into the final set of requested target platforms.
        public static List<string> GetRequestedTargetPlatforms(Arguments arguments)
        {
            Argument? targetPlatformsArgument = arguments.GetArgument("TargetPlatforms");
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

        // Keep the current UI options, but normalize them into a generic target-platform list for validation.
        public static List<string> GetSelectedTargetPlatforms(PluginBuildOptions pluginBuildOptions)
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
