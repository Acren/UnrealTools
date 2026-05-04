using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using LocalAutomation.Core.IO;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    /// <summary>
    /// Builds and assembles a distributable plugin package while exposing each UBT compile command as its own task.
    /// </summary>
    internal sealed class PackageDistributablePlugin : UnrealOperation<Plugin>
    {
        // BuildPlugin packages development binaries for all requested game target platforms.
        private const string DevelopmentConfigurationName = "Development";

        // BuildPlugin packages shipping binaries for all requested game target platforms.
        private const string ShippingConfigurationName = "Shipping";

        // These are the built-in restricted folder names Unreal excludes from BuildPlugin package payloads.
        private static readonly IReadOnlySet<string> RestrictedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "EpicInternal",
            "CarefullyRedist",
            "LimitedAccess",
            "NotForLicensees",
            "NoRedist"
        };

        /// <summary>
        /// Distributable plugin packaging needs platform and strict-include options but intentionally ignores raw UAT arguments.
        /// </summary>
        protected override IEnumerable<Type> GetDeclaredOptionSetTypes(IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(PluginBuildOptions)
                });
        }

        /// <summary>
        /// Validates the selected engine and requested target platforms before authoring UBT command tasks.
        /// </summary>
        protected override string? CheckRequirementsSatisfied(ValidatedOperationParameters operationParameters)
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

        /// <summary>
        /// Authors host-project preparation, direct parallel compile commands, and the package-copy task.
        /// </summary>
        protected override void DescribeExecutionPlan(ValidatedOperationParameters operationParameters, ExecutionTaskBuilder root)
        {
            IReadOnlyList<PluginBuildStep> buildSteps = CreateBuildSteps(operationParameters);
            root.Children(steps =>
            {
                steps.Task("Prepare Host Project")
                    .Describe("Ensure the BuildPlugin-style host project descriptor exists before compiling")
                    .Run(PrepareHostProjectAsync);
            });

            if (buildSteps.Count > 0)
            {
                // Build steps are direct children so the graph shows the actionable UBT commands without an extra wrapper.
                root.Children(ExecutionChildMode.Parallel, compileSteps =>
                {
                    foreach (PluginBuildStep buildStep in buildSteps)
                    {
                        PluginBuildStep capturedStep = buildStep;
                        compileSteps.Task(capturedStep.Title)
                            .Describe(capturedStep.Description)
                            .WithExecutionLocks(UnrealExecutionLocks.GlobalBuild)
                            .Run(context => RunBuildStepAsync(context, capturedStep));
                    }
                });
            }
            else
            {
                root.Children(steps =>
                {
                    steps.Task("Skip Plugin Binary Compile")
                        .Describe("No plugin modules require UBT compilation before packaging")
                        .Run(context =>
                        {
                            context.Logger.LogInformation("Distributable plugin package has no module compile steps.");
                            return Task.CompletedTask;
                        });
                });
            }

            root.Children(steps =>
            {
                steps.Task("Package Distributable Plugin Payload")
                    .Describe("Copy the distributable plugin payload out of the generated host project")
                    .Run(context => PackageAsync(context, buildSteps));
            });
        }

        /// <summary>
        /// Ensures the plugin's inferred host project contains the descriptor required by Build.bat plugin compilation.
        /// </summary>
        private Task PrepareHostProjectAsync(ExecutionTaskContext context)
        {
            Plugin plugin = GetRequiredTarget(context.ValidatedOperationParameters);
            using Project hostProject = Project.CreateEmpty(plugin.HostProjectPath, "HostProject");
            hostProject.SetPluginEnabled(plugin.Name, true);
            context.Logger.LogInformation("Prepared BuildPlugin host project '{HostProjectPath}' for plugin '{PluginPath}'.", plugin.HostProjectPath, plugin.PluginPath);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs one UBT build command and fails if the expected manifest was not produced.
        /// </summary>
        private async Task<OperationResult> RunBuildStepAsync(ExecutionTaskContext context, PluginBuildStep buildStep)
        {
            string manifestPath = GetManifestPath(context, buildStep);
            FileUtils.DeleteFileIfExists(manifestPath);
            Command command = BuildCommand(context, buildStep, manifestPath);
            OperationResult result = await CommandProcessExecutor.ExecuteAsync(context, command, GetType().Name).ConfigureAwait(false);
            if (result.Outcome != ExecutionTaskOutcome.Completed)
            {
                return result;
            }

            if (File.Exists(manifestPath))
            {
                return result;
            }

            context.Logger.LogError("Distributable plugin build step '{BuildStepTitle}' did not produce manifest '{ManifestPath}'.", buildStep.Title, manifestPath);
            return OperationResult.Failed();
        }

        /// <summary>
        /// Builds the direct Build.bat command that mirrors the UBT invocation used by Unreal BuildPlugin.
        /// </summary>
        private Command BuildCommand(ExecutionTaskContext context, PluginBuildStep buildStep, string manifestPath)
        {
            Plugin plugin = GetRequiredTarget(context.ValidatedOperationParameters);
            Project hostProject = plugin.GetHostProjectForDiagnostics();
            Engine engine = GetRequiredTargetEngineInstall(context.ValidatedOperationParameters);
            PluginBuildOptions pluginBuildOptions = context.ValidatedOperationParameters.GetOptions<PluginBuildOptions>();
            Arguments arguments = new();
            string targetName = ResolveTargetName(engine, buildStep);

            // Build.bat forwards the leading target/platform/configuration/project tuple directly to UnrealBuildTool.
            arguments.SetArgument(targetName);
            arguments.SetArgument(buildStep.Platform);
            arguments.SetArgument(buildStep.Configuration);
            arguments.SetPath(hostProject.UProjectPath);

            // The plugin and manifest switches are the core BuildPlugin contract that gives packaging an exact product list.
            arguments.SetKeyPath("plugin", plugin.UPluginPath);
            if (engine.Version.MajorVersion < 5)
            {
                arguments.SetFlag("iwyu");
            }

            arguments.SetFlag("noubtmakefiles");
            arguments.SetKeyPath("manifest", manifestPath);
            arguments.SetFlag("nohotreload");

            if (pluginBuildOptions.StrictIncludes)
            {
                arguments.SetFlag("NoPCH");
                arguments.SetFlag("NoSharedPCH");
                arguments.SetFlag("DisableUnity");
            }

            return new Command(engine.GetBuildPath(), arguments.ToString());
        }

        /// <summary>
        /// Creates the UBT compile steps required for the plugin's declared module shape and requested target platforms.
        /// </summary>
        private IReadOnlyList<PluginBuildStep> CreateBuildSteps(ValidatedOperationParameters operationParameters)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            List<PluginBuildStep> buildSteps = new();

            if (plugin.IsBlueprintOnly)
            {
                return buildSteps;
            }

            buildSteps.Add(CreateBuildStep("Build Host Win64", PluginBuildTargetKind.HostEditor, "Win64", DevelopmentConfigurationName));

            if (!plugin.HasRuntimeModules)
            {
                return buildSteps;
            }

            foreach (string targetPlatform in PluginBuildPlatformValidation.GetSelectedTargetPlatforms(operationParameters.GetOptions<PluginBuildOptions>()))
            {
                buildSteps.Add(CreateBuildStep($"Build {targetPlatform} Development", PluginBuildTargetKind.Game, targetPlatform, DevelopmentConfigurationName));
                buildSteps.Add(CreateBuildStep($"Build {targetPlatform} Shipping", PluginBuildTargetKind.Game, targetPlatform, ShippingConfigurationName));
            }

            return buildSteps;
        }

        /// <summary>
        /// Creates one build-step descriptor whose target-specific paths are resolved from the live runtime parameters.
        /// </summary>
        private static PluginBuildStep CreateBuildStep(string title, PluginBuildTargetKind targetKind, string platform, string configuration)
        {
            return new PluginBuildStep(
                title,
                $"Build the distributable plugin for {platform} {configuration} and write its product manifest",
                targetKind,
                platform,
                configuration);
        }

        /// <summary>
        /// Resolves the target name against the live engine so statically imported tasks can run against runtime parameters.
        /// </summary>
        private static string ResolveTargetName(Engine engine, PluginBuildStep buildStep)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            _ = buildStep ?? throw new ArgumentNullException(nameof(buildStep));
            return buildStep.TargetKind == PluginBuildTargetKind.HostEditor
                ? engine.BaseEditorName
                : engine.Version.MajorVersion >= 5 ? "UnrealGame" : "UE4Game";
        }

        /// <summary>
        /// Resolves the build-product manifest path from the live plugin target instead of the authoring-time target.
        /// </summary>
        private string GetManifestPath(ExecutionTaskContext context, PluginBuildStep buildStep)
        {
            Plugin plugin = GetRequiredTarget(context.ValidatedOperationParameters);
            Engine engine = GetRequiredTargetEngineInstall(context.ValidatedOperationParameters);
            string targetName = ResolveTargetName(engine, buildStep);
            string savedPath = Path.Combine(plugin.HostProject.ProjectPath, "Saved");
            return Path.Combine(savedPath, $"Manifest-{targetName}-{buildStep.Platform}-{buildStep.Configuration}.xml");
        }

        /// <summary>
        /// Copies package files selected from authored plugin inputs, generated headers, and UBT build manifests.
        /// </summary>
        private Task PackageAsync(ExecutionTaskContext context, IReadOnlyList<PluginBuildStep> buildSteps)
        {
            Plugin plugin = GetRequiredTarget(context.ValidatedOperationParameters);
            Engine engine = GetRequiredTargetEngineInstall(context.ValidatedOperationParameters);
            string packagePath = GetOutputPath(context.ValidatedOperationParameters);
            HashSet<string> relativeFiles = SelectFilteredPackageFiles(context, plugin, engine, buildSteps);

            FileUtils.DeleteDirectoryIfExists(packagePath, context.Logger);
            FileUtils.MaterializeFiles(plugin.PluginPath, packagePath, relativeFiles, context.Logger, context.CancellationToken);
            PatchPackagedDescriptor(packagePath, Path.GetFileName(plugin.UPluginPath), engine);
            context.Logger.LogInformation("Packaged distributable plugin payload to '{PackagePath}' with {FileCount} file(s).", packagePath, relativeFiles.Count);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Selects plugin-relative package files using the same default includes, custom FilterPlugin rules, and restricted
        /// folder exclusions that Unreal BuildPlugin applies before copying the package payload.
        /// </summary>
        private HashSet<string> SelectFilteredPackageFiles(ExecutionTaskContext context, Plugin plugin, Engine engine, IReadOnlyList<PluginBuildStep> buildSteps)
        {
            string pluginPath = plugin.PluginPath;
            List<(Regex Pattern, bool Include)> rules = new();

            // BuildPlugin starts from an empty/default-exclude filter, then explicitly includes the descriptor and products.
            AddFilterRuleForFile(rules, Path.GetFileName(plugin.UPluginPath), include: true);
            AddBuildProductFilterRules(context, pluginPath, rules, buildSteps, context.Logger);

            // These default package rules intentionally match BuildPlugin; Config and Extras are opt-in via FilterPlugin.ini.
            AddFilterRule(rules, "/Binaries/ThirdParty/...", include: true);
            AddFilterRule(rules, "/Resources/...", include: true);
            AddFilterRule(rules, "/Content/...", include: true);
            AddFilterRule(rules, "/Intermediate/Build/.../Inc/...", include: true);
            AddFilterRule(rules, "/Shaders/...", include: true);
            AddFilterRule(rules, "/Source/...", include: true);
            AddFilterRule(rules, "/Tests/...", include: false);

            // BuildPlugin reads the all-platform filter first, then UE5+ reads each targeted platform-specific filter.
            AddRulesFromFilterFile(pluginPath, "FilterPlugin.ini", rules, context.Logger);
            if (engine.Version.MajorVersion >= 5)
            {
                PluginBuildOptions pluginBuildOptions = context.ValidatedOperationParameters.GetOptions<PluginBuildOptions>();
                foreach (string targetPlatform in PluginBuildPlatformValidation.GetSelectedTargetPlatforms(pluginBuildOptions))
                {
                    AddRulesFromFilterFile(pluginPath, $"FilterPlugin{targetPlatform}.ini", rules, context.Logger);
                }
            }

            // Restricted folders are excluded after custom rules, so plugin filters cannot re-include non-redist content.
            foreach (string restrictedFolderName in RestrictedFolderNames)
            {
                AddFilterRule(rules, $".../{restrictedFolderName}/...", include: false);
            }

            return Directory.GetFiles(pluginPath, "*", SearchOption.AllDirectories)
                .Select(filePath => NormalizeRelativePackagePath(Path.GetRelativePath(pluginPath, filePath)))
                .Where(relativePath => MatchesFilterRules(relativePath, rules))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Adds exact include rules for every manifest build product that lives inside the plugin package root.
        /// </summary>
        private void AddBuildProductFilterRules(ExecutionTaskContext context, string pluginPath, ICollection<(Regex Pattern, bool Include)> rules, IReadOnlyList<PluginBuildStep> buildSteps, ILogger logger)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            foreach (PluginBuildStep buildStep in buildSteps)
            {
                string manifestPath = GetManifestPath(context, buildStep);
                foreach (string buildProductPath in ReadBuildProducts(manifestPath))
                {
                    if (!File.Exists(buildProductPath))
                    {
                        throw new FileNotFoundException($"Build product listed in manifest does not exist: {buildProductPath}", buildProductPath);
                    }

                    if (IsSameOrDescendantPath(pluginPath, buildProductPath))
                    {
                        AddFilterRuleForFile(rules, Path.GetRelativePath(pluginPath, buildProductPath), include: true);
                    }
                    else
                    {
                        logger.LogDebug("Ignoring build product outside plugin package root: {BuildProductPath}", buildProductPath);
                    }
                }
            }
        }

        /// <summary>
        /// Reads one Config/FilterPlugin*.ini file and adds rules from its [FilterPlugin] section when the file exists.
        /// </summary>
        private static void AddRulesFromFilterFile(string pluginPath, string filterFileName, ICollection<(Regex Pattern, bool Include)> rules, ILogger logger)
        {
            string filterPath = Path.Combine(pluginPath, "Config", filterFileName);
            if (!File.Exists(filterPath))
            {
                return;
            }

            logger.LogInformation("Reading filter rules from {FilterFile}", filterPath);
            bool inFilterPluginSection = false;
            foreach (string line in File.ReadLines(filterPath))
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.Length == 0 || trimmedLine.StartsWith(";", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmedLine.StartsWith("[", StringComparison.Ordinal))
                {
                    inFilterPluginSection = string.Equals(trimmedLine, "[FilterPlugin]", StringComparison.Ordinal);
                    continue;
                }

                if (inFilterPluginSection)
                {
                    AddFilterRuleFromLine(rules, trimmedLine);
                }
            }
        }

        /// <summary>
        /// Adds one FilterPlugin line, including '-' excludes and ignoring tag-gated rules because BuildPlugin supplies no tags.
        /// </summary>
        private static void AddFilterRuleFromLine(ICollection<(Regex Pattern, bool Include)> rules, string line)
        {
            string cleanRule = line.Trim();
            if (cleanRule.StartsWith("{", StringComparison.Ordinal))
            {
                // BuildPlugin passes no allow-tags for FilterPlugin files, so every tagged rule is inactive.
                return;
            }

            bool include = true;
            if (cleanRule.StartsWith("-", StringComparison.Ordinal))
            {
                include = false;
                cleanRule = cleanRule.Substring(1).TrimStart();
            }

            AddFilterRule(rules, cleanRule, include);
        }

        /// <summary>
        /// Adds one exact file rule after converting the relative path to BuildPlugin's slash-prefixed filter syntax.
        /// </summary>
        private static void AddFilterRuleForFile(ICollection<(Regex Pattern, bool Include)> rules, string relativePath, bool include)
        {
            AddFilterRule(rules, "/" + NormalizeRelativePackagePath(relativePath), include);
        }

        /// <summary>
        /// Adds one wildcard rule using BuildPlugin-style normalization and last-match-wins ordering.
        /// </summary>
        private static void AddFilterRule(ICollection<(Regex Pattern, bool Include)> rules, string pattern, bool include)
        {
            string normalizedPattern = NormalizeFilterPattern(pattern);
            Regex regex = new("^" + ConvertFilterPatternToRegex(normalizedPattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            rules.Add((regex, include));
        }

        /// <summary>
        /// Applies BuildPlugin's rule normalization for rooted paths, filename-only rules, and trailing directory slashes.
        /// </summary>
        private static string NormalizeFilterPattern(string pattern)
        {
            string normalizedPattern = pattern.Trim().Replace('\\', '/');
            if (normalizedPattern.StartsWith("/", StringComparison.Ordinal))
            {
                normalizedPattern = normalizedPattern.Substring(1);
            }
            else if (!normalizedPattern.Contains('/', StringComparison.Ordinal) && !normalizedPattern.StartsWith("...", StringComparison.Ordinal))
            {
                normalizedPattern = ".../" + normalizedPattern;
            }

            if (normalizedPattern.EndsWith("/", StringComparison.Ordinal))
            {
                normalizedPattern += "...";
            }

            return normalizedPattern;
        }

        /// <summary>
        /// Converts BuildPlugin filter wildcards into a regex over normalized plugin-relative file paths.
        /// </summary>
        private static string ConvertFilterPatternToRegex(string normalizedPattern)
        {
            System.Text.StringBuilder builder = new();
            for (int index = 0; index < normalizedPattern.Length; index += 1)
            {
                if (index + 2 < normalizedPattern.Length && normalizedPattern[index] == '.' && normalizedPattern[index + 1] == '.' && normalizedPattern[index + 2] == '.')
                {
                    if (index + 3 < normalizedPattern.Length && normalizedPattern[index + 3] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        index += 3;
                    }
                    else
                    {
                        builder.Append(".*");
                        index += 2;
                    }

                    continue;
                }

                char character = normalizedPattern[index];
                if (character == '*')
                {
                    builder.Append("[^/]*");
                }
                else if (character == '?')
                {
                    builder.Append("[^/]");
                }
                else
                {
                    builder.Append(Regex.Escape(character.ToString()));
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Evaluates one normalized plugin-relative file path against ordered BuildPlugin-style rules.
        /// </summary>
        private static bool MatchesFilterRules(string relativePath, IEnumerable<(Regex Pattern, bool Include)> rules)
        {
            bool include = false;
            foreach ((Regex pattern, bool ruleIncludes) in rules)
            {
                if (pattern.IsMatch(relativePath))
                {
                    include = ruleIncludes;
                }
            }

            return include;
        }

        /// <summary>
        /// Normalizes plugin-relative paths to the forward-slash form consumed by BuildPlugin filters.
        /// </summary>
        private static string NormalizeRelativePackagePath(string relativePath)
        {
            return relativePath.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/').TrimStart('/');
        }

        /// <summary>
        /// Reads the BuildProducts entries from one UBT XML manifest.
        /// </summary>
        private static IEnumerable<string> ReadBuildProducts(string manifestPath)
        {
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException($"Required build manifest is missing: {manifestPath}", manifestPath);
            }

            XDocument manifest = XDocument.Load(manifestPath);
            XElement? buildProducts = manifest.Descendants().FirstOrDefault(element => element.Name.LocalName == "BuildProducts");
            if (buildProducts == null)
            {
                return Array.Empty<string>();
            }

            return buildProducts.Elements()
                .Select(element => element.Value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        /// <summary>
        /// Applies the descriptor edits that mark the copied payload as an installed engine-versioned plugin package.
        /// </summary>
        private static void PatchPackagedDescriptor(string packagePath, string pluginDescriptorFileName, Engine engine)
        {
            string descriptorPath = Path.Combine(packagePath, pluginDescriptorFileName);
            JObject descriptor = JObject.Parse(File.ReadAllText(descriptorPath));
            descriptor.Remove("EnabledByDefault");
            descriptor["Installed"] = true;
            descriptor["EngineVersion"] = engine.Version.WithPatch(0).ToString();
            File.WriteAllText(descriptorPath, descriptor.ToString(Formatting.Indented));
        }

        /// <summary>
        /// Returns whether the candidate path is the source root itself or a descendant of that root.
        /// </summary>
        private static bool IsSameOrDescendantPath(string sourcePath, string candidatePath)
        {
            string normalizedSourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
            string normalizedCandidatePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            string sourceWithSeparator = normalizedSourcePath + Path.DirectorySeparatorChar;
            return string.Equals(normalizedSourcePath, normalizedCandidatePath, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidatePath.StartsWith(sourceWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Identifies the live Unreal target family used by a distributable plugin compile step.
        /// </summary>
        private enum PluginBuildTargetKind
        {
            // The host editor target name is engine-specific and resolves from the live engine install.
            HostEditor,

            // The game target name changed between UE4 and UE5, so it resolves from the live engine install.
            Game
        }

        /// <summary>
        /// Describes one direct UBT compile task without storing paths that depend on the runtime cache workspace.
        /// </summary>
        private sealed class PluginBuildStep
        {
            /// <summary>
            /// Creates one immutable build-step descriptor.
            /// </summary>
            internal PluginBuildStep(string title, string description, PluginBuildTargetKind targetKind, string platform, string configuration)
            {
                Title = title;
                Description = description;
                TargetKind = targetKind;
                Platform = platform;
                Configuration = configuration;
            }

            /// <summary>
            /// Gets the visible task title for this compile command.
            /// </summary>
            internal string Title { get; }

            /// <summary>
            /// Gets the visible task description for this compile command.
            /// </summary>
            internal string Description { get; }

            /// <summary>
            /// Gets the target family used to resolve the first Build.bat argument against the live engine.
            /// </summary>
            internal PluginBuildTargetKind TargetKind { get; }

            /// <summary>
            /// Gets the target platform argument passed to UnrealBuildTool.
            /// </summary>
            internal string Platform { get; }

            /// <summary>
            /// Gets the build configuration argument passed to UnrealBuildTool.
            /// </summary>
            internal string Configuration { get; }
        }
    }
}
