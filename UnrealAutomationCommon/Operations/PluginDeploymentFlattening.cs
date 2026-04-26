using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LocalAutomation.Core.IO;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Builds a staged single-plugin payload by embedding configured sibling plugins as renamed modules.
    /// </summary>
    internal static class PluginDeploymentFlattening
    {
        // Merge options accept several delimiters so values remain readable in a single property-grid text box.
        private static readonly char[] MergeEntryDelimiters = { ',', ';', '\r', '\n' };

        // Unreal module names and reflected type prefixes must remain valid C++ identifier fragments after rewriting.
        private static readonly Regex IdentifierRegex = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

        // Pascal-case tokenization lets the generated embedded name remove only whole shared name tokens.
        private static readonly Regex PascalTokenRegex = new("[A-Z]+(?=[A-Z][a-z]|$)|[A-Z]?[a-z]+|[0-9]+", RegexOptions.Compiled);

        // Reflected classes and interfaces use class redirects because their serialized identity is a UObject class.
        private static readonly Regex ReflectedClassRegex = new(@"\bU(?:CLASS|INTERFACE)\b.*?\bclass\s+(?:\w+_API\s+)?(?<Name>[UA]\w+)", RegexOptions.Compiled | RegexOptions.Singleline);

        // Reflected structs use struct redirects and conventionally carry an F prefix in C++.
        private static readonly Regex ReflectedStructRegex = new(@"\bUSTRUCT\b.*?\bstruct\s+(?:\w+_API\s+)?(?<Name>F\w+)", RegexOptions.Compiled | RegexOptions.Singleline);

        // Reflected enums use enum redirects and keep their E prefix in Unreal's reflected path names.
        private static readonly Regex ReflectedEnumRegex = new(@"\bUENUM\b.*?\benum(?:\s+class)?\s+(?:\w+_API\s+)?(?<Name>E\w+)", RegexOptions.Compiled | RegexOptions.Singleline);

        // Source and config files are the text payloads that may legally contain module names, include paths, and redirects.
        private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".c",
            ".cc",
            ".cpp",
            ".cs",
            ".h",
            ".hh",
            ".hpp",
            ".inl",
            ".ini",
            ".md",
            ".txt",
            ".usf",
            ".ush"
        };

        /// <summary>
        /// Copies every configured merge plugin into the workspace project so engine-specific staging reads only isolated
        /// workspace inputs.
        /// </summary>
        public static void MaterializeMergePlugins(Project sourceProject, Project workspaceProject, string targetPluginName, PluginDeployOptions deployOptions, ILogger logger)
        {
            IReadOnlyList<MergeRule> mergeRules = ParseMergeRules(deployOptions.MergePlugins);
            if (mergeRules.Count == 0)
            {
                return;
            }

            // Workspace plugins live beside the target plugin so later staging can resolve every merge input uniformly.
            Directory.CreateDirectory(workspaceProject.PluginsPath);
            HashSet<string> materializedPluginNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (MergeRule mergeRule in mergeRules)
            {
                using Plugin sourcePlugin = ResolveMergePlugin(sourceProject, mergeRule.SourcePluginSpecifier);
                if (sourcePlugin.Name.Equals(targetPluginName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Deploy plugin cannot merge the target plugin into itself: {sourcePlugin.Name}");
                }

                if (!materializedPluginNames.Add(sourcePlugin.Name))
                {
                    throw new InvalidOperationException($"Deploy plugin merge list contains duplicate plugin '{sourcePlugin.Name}'.");
                }

                string workspaceMergePluginPath = Path.Combine(workspaceProject.PluginsPath, sourcePlugin.Name);
                logger.LogInformation("Materializing merge plugin '{PluginName}' into workspace: {WorkspacePluginPath}", sourcePlugin.Name, workspaceMergePluginPath);
                FileUtils.DeleteDirectoryIfExists(workspaceMergePluginPath);
                FileUtils.MaterializeDirectory(sourcePlugin.PluginPath, workspaceMergePluginPath, MaterializationSpecs.CreatePlugin(sourcePlugin), logger);
            }
        }

        /// <summary>
        /// Applies the configured merge plugins to one staged plugin copy before RunUAT BuildPlugin consumes it.
        /// </summary>
        public static void ApplyToStagedPlugin(Plugin stagingPlugin, Project workspaceProject, PluginDeployOptions deployOptions, ILogger logger)
        {
            IReadOnlyList<MergeRule> mergeRules = ParseMergeRules(deployOptions.MergePlugins);
            if (mergeRules.Count == 0)
            {
                return;
            }

            // Resolve all merge inputs up front so naming and collision validation sees the complete merge set.
            IReadOnlyList<ResolvedMergePlugin> mergePlugins = ResolveMergePlugins(workspaceProject, mergeRules);
            IReadOnlyList<ModuleRename> moduleRenames = BuildModuleRenames(stagingPlugin, mergePlugins);
            IReadOnlyList<ReflectedRename> reflectedRenames = moduleRenames.SelectMany(FindReflectedRenames).ToList();
            IReadOnlyList<ReplacementRule> replacements = BuildReplacementRules(moduleRenames, reflectedRenames);

            logger.LogInformation("Flattening {MergePluginCount} merge plugin(s) into staged plugin '{PluginName}'.", mergePlugins.Count, stagingPlugin.Name);
            ValidateUnsupportedPayloads(mergePlugins);
            CopyRenamedModules(stagingPlugin, moduleRenames, logger);
            RewriteTextFiles(stagingPlugin, replacements, logger);
            RenamePaths(stagingPlugin, replacements, logger);
            UpdateStagedPluginDescriptor(stagingPlugin, mergePlugins, moduleRenames);
            AppendMergedConfigFiles(stagingPlugin, mergePlugins, replacements, logger);
            AppendCoreRedirects(stagingPlugin, reflectedRenames, logger);
            ValidateFlattenedStaging(stagingPlugin, mergePlugins, moduleRenames);
        }

        /// <summary>
        /// Parses the target-local merge option into structured source plugin specifiers and optional embedded prefixes.
        /// </summary>
        private static IReadOnlyList<MergeRule> ParseMergeRules(string mergePlugins)
        {
            if (string.IsNullOrWhiteSpace(mergePlugins))
            {
                return Array.Empty<MergeRule>();
            }

            // Empty entries are ignored so users can format the option value across multiple readable lines.
            List<MergeRule> rules = new();
            foreach (string rawEntry in mergePlugins.Split(MergeEntryDelimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = rawEntry.Split(new[] { '=' }, 2, StringSplitOptions.TrimEntries);
                string sourcePluginSpecifier = parts[0];
                string? embeddedPrefixOverride = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;
                if (string.IsNullOrWhiteSpace(sourcePluginSpecifier))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(embeddedPrefixOverride))
                {
                    ValidateIdentifier(embeddedPrefixOverride!, $"embedded prefix override for merge plugin '{sourcePluginSpecifier}'");
                }

                rules.Add(new MergeRule(sourcePluginSpecifier, embeddedPrefixOverride));
            }

            return rules;
        }

        /// <summary>
        /// Resolves every parsed rule against the workspace project and captures descriptor data without retaining file watchers.
        /// </summary>
        private static IReadOnlyList<ResolvedMergePlugin> ResolveMergePlugins(Project workspaceProject, IReadOnlyList<MergeRule> mergeRules)
        {
            List<ResolvedMergePlugin> mergePlugins = new();
            HashSet<string> mergePluginNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (MergeRule mergeRule in mergeRules)
            {
                using Plugin mergePlugin = ResolveMergePlugin(workspaceProject, mergeRule.SourcePluginSpecifier);
                if (!mergePluginNames.Add(mergePlugin.Name))
                {
                    throw new InvalidOperationException($"Deploy plugin merge list contains duplicate plugin '{mergePlugin.Name}'.");
                }

                JObject descriptor = JObject.Parse(File.ReadAllText(mergePlugin.UPluginPath));
                mergePlugins.Add(new ResolvedMergePlugin(mergePlugin.Name, mergePlugin.PluginPath, mergeRule.EmbeddedPrefixOverride, descriptor));
            }

            return mergePlugins;
        }

        /// <summary>
        /// Resolves one merge plugin by explicit path or by plugin directory name under the project Plugins tree.
        /// </summary>
        private static Plugin ResolveMergePlugin(Project project, string sourcePluginSpecifier)
        {
            string? pluginPath = ResolveMergePluginPath(project, sourcePluginSpecifier);
            if (pluginPath == null)
            {
                throw new DirectoryNotFoundException($"Could not resolve merge plugin '{sourcePluginSpecifier}' under '{project.PluginsPath}'.");
            }

            return new Plugin(pluginPath);
        }

        /// <summary>
        /// Finds a merge plugin path from explicit path candidates first, then by recursive plugin-name search.
        /// </summary>
        private static string? ResolveMergePluginPath(Project project, string sourcePluginSpecifier)
        {
            foreach (string candidatePath in GetExplicitPluginPathCandidates(project, sourcePluginSpecifier))
            {
                if (PluginPaths.Instance.IsTargetDirectory(candidatePath))
                {
                    return Path.GetFullPath(candidatePath);
                }
            }

            if (!Directory.Exists(project.PluginsPath))
            {
                return null;
            }

            // Name lookup is recursive so grouped project plugin folders are supported without making users type paths.
            string pluginLookupName = GetPluginLookupName(sourcePluginSpecifier);
            string[] matchingPluginPaths = Directory.GetDirectories(project.PluginsPath, "*", SearchOption.AllDirectories)
                .Where(PluginPaths.Instance.IsTargetDirectory)
                .Where(path => string.Equals(Path.GetFileName(path), pluginLookupName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matchingPluginPaths.Length > 1)
            {
                throw new InvalidOperationException($"Merge plugin '{sourcePluginSpecifier}' is ambiguous: {string.Join(", ", matchingPluginPaths)}");
            }

            return matchingPluginPaths.SingleOrDefault();
        }

        /// <summary>
        /// Converts a path-like merge specifier back to the plugin directory name used inside the workspace copy.
        /// </summary>
        private static string GetPluginLookupName(string sourcePluginSpecifier)
        {
            string trimmedSpecifier = Path.TrimEndingDirectorySeparator(sourcePluginSpecifier);
            string fileName = Path.GetFileName(trimmedSpecifier);
            return string.IsNullOrWhiteSpace(fileName) ? sourcePluginSpecifier : fileName;
        }

        /// <summary>
        /// Returns the explicit path interpretations that are useful before falling back to plugin-name lookup.
        /// </summary>
        private static IEnumerable<string> GetExplicitPluginPathCandidates(Project project, string sourcePluginSpecifier)
        {
            if (Path.IsPathRooted(sourcePluginSpecifier))
            {
                yield return sourcePluginSpecifier;
                yield break;
            }

            // Relative plugin paths are interpreted from the host project first because deploy options are target-local.
            yield return Path.Combine(project.ProjectPath, sourcePluginSpecifier);
            yield return Path.Combine(project.PluginsPath, sourcePluginSpecifier);
        }

        /// <summary>
        /// Creates the complete module rename set while validating that every generated module name is unique.
        /// </summary>
        private static IReadOnlyList<ModuleRename> BuildModuleRenames(Plugin stagingPlugin, IReadOnlyList<ResolvedMergePlugin> mergePlugins)
        {
            JObject stagedDescriptor = JObject.Parse(File.ReadAllText(stagingPlugin.UPluginPath));
            HashSet<string> claimedModuleNames = GetModuleNames(stagedDescriptor).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<ModuleRename> moduleRenames = new();

            foreach (ResolvedMergePlugin mergePlugin in mergePlugins)
            {
                string embeddedPrefix = mergePlugin.EmbeddedPrefixOverride ?? DeriveEmbeddedPrefix(mergePlugin.Name, stagingPlugin.Name);
                ValidateIdentifier(embeddedPrefix, $"generated embedded prefix for merge plugin '{mergePlugin.Name}'");
                List<JObject> moduleDeclarations = GetModuleDeclarations(mergePlugin.Descriptor).ToList();
                if (moduleDeclarations.Count == 0)
                {
                    throw new InvalidOperationException($"Merge plugin '{mergePlugin.Name}' does not declare any source modules to embed.");
                }

                foreach (JObject moduleDeclaration in moduleDeclarations)
                {
                    string sourceModuleName = GetRequiredName(moduleDeclaration, "merge plugin module");
                    if (!sourceModuleName.StartsWith(mergePlugin.Name, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Module '{sourceModuleName}' in merge plugin '{mergePlugin.Name}' must start with '{mergePlugin.Name}' so Deploy Plugin can generate a co-installable embedded name.");
                    }

                    string embeddedModuleName = embeddedPrefix + sourceModuleName.Substring(mergePlugin.Name.Length);
                    ValidateIdentifier(embeddedModuleName, $"embedded module name generated from '{sourceModuleName}'");
                    if (!claimedModuleNames.Add(embeddedModuleName))
                    {
                        throw new InvalidOperationException($"Embedded module name '{embeddedModuleName}' collides with an existing staged module.");
                    }

                    moduleRenames.Add(new ModuleRename(mergePlugin, moduleDeclaration, sourceModuleName, embeddedModuleName, embeddedPrefix));
                }
            }

            return moduleRenames;
        }

        /// <summary>
        /// Derives the embedded prefix by appending the host name suffix that remains after removing shared leading tokens.
        /// </summary>
        private static string DeriveEmbeddedPrefix(string sourcePluginName, string hostPluginName)
        {
            string[] sourceTokens = SplitIdentifierTokens(sourcePluginName);
            string[] hostTokens = SplitIdentifierTokens(hostPluginName);
            int sharedTokenCount = 0;
            while (sharedTokenCount < sourceTokens.Length && sharedTokenCount < hostTokens.Length && string.Equals(sourceTokens[sharedTokenCount], hostTokens[sharedTokenCount], StringComparison.Ordinal))
            {
                sharedTokenCount++;
            }

            string hostSuffix = string.Concat(hostTokens.Skip(sharedTokenCount));
            if (string.IsNullOrWhiteSpace(hostSuffix))
            {
                throw new InvalidOperationException($"Could not derive an embedded prefix for merge plugin '{sourcePluginName}' in host plugin '{hostPluginName}'. Add an explicit 'PluginName=EmbeddedPrefix' merge entry.");
            }

            return sourcePluginName + hostSuffix;
        }

        /// <summary>
        /// Splits an identifier into Pascal-style tokens, falling back to the original value when tokenization finds none.
        /// </summary>
        private static string[] SplitIdentifierTokens(string value)
        {
            string[] tokens = PascalTokenRegex.Matches(value).Select(match => match.Value).ToArray();
            return tokens.Length > 0 ? tokens : new[] { value };
        }

        /// <summary>
        /// Rejects merge plugin payloads that cannot be made safe by source-module flattening alone.
        /// </summary>
        private static void ValidateUnsupportedPayloads(IReadOnlyList<ResolvedMergePlugin> mergePlugins)
        {
            foreach (ResolvedMergePlugin mergePlugin in mergePlugins)
            {
                string contentPath = Path.Combine(mergePlugin.PluginPath, "Content");
                if (Directory.Exists(contentPath) && Directory.EnumerateFiles(contentPath, "*", SearchOption.AllDirectories).Any())
                {
                    throw new InvalidOperationException($"Merge plugin '{mergePlugin.Name}' contains Content assets. Deploy Plugin flattening only embeds source modules and config because moving plugin content changes Unreal package paths.");
                }
            }
        }

        /// <summary>
        /// Copies each source module to its generated embedded module directory in the staged plugin.
        /// </summary>
        private static void CopyRenamedModules(Plugin stagingPlugin, IReadOnlyList<ModuleRename> moduleRenames, ILogger logger)
        {
            string stagedSourcePath = Path.Combine(stagingPlugin.PluginPath, "Source");
            Directory.CreateDirectory(stagedSourcePath);

            foreach (ModuleRename moduleRename in moduleRenames)
            {
                string sourceModulePath = moduleRename.SourceModulePath;
                if (!Directory.Exists(sourceModulePath))
                {
                    throw new DirectoryNotFoundException($"Merge plugin module source directory is missing: {sourceModulePath}");
                }

                string embeddedModulePath = Path.Combine(stagedSourcePath, moduleRename.EmbeddedModuleName);
                if (Directory.Exists(embeddedModulePath))
                {
                    throw new InvalidOperationException($"Embedded module directory already exists: {embeddedModulePath}");
                }

                logger.LogInformation("Copying merge module '{SourceModule}' to embedded module '{EmbeddedModule}'.", moduleRename.SourceModuleName, moduleRename.EmbeddedModuleName);
                FileUtils.CopyDirectory(sourceModulePath, embeddedModulePath);
            }
        }

        /// <summary>
        /// Finds reflected type declarations in the original merge source so redirects can map serialized asset references.
        /// </summary>
        private static IReadOnlyList<ReflectedRename> FindReflectedRenames(ModuleRename moduleRename)
        {
            List<ReflectedRename> reflectedRenames = new();
            foreach (string filePath in Directory.EnumerateFiles(moduleRename.SourceModulePath, "*.*", SearchOption.AllDirectories).Where(IsTextFile))
            {
                string content = File.ReadAllText(filePath);
                reflectedRenames.AddRange(FindReflectedRenames(content, filePath, moduleRename, ReflectedRedirectKind.Class, ReflectedClassRegex));
                reflectedRenames.AddRange(FindReflectedRenames(content, filePath, moduleRename, ReflectedRedirectKind.Struct, ReflectedStructRegex));
                reflectedRenames.AddRange(FindReflectedRenames(content, filePath, moduleRename, ReflectedRedirectKind.Enum, ReflectedEnumRegex));
            }

            return reflectedRenames;
        }

        /// <summary>
        /// Converts regex matches for one reflected declaration kind into concrete rename and redirect entries.
        /// </summary>
        private static IEnumerable<ReflectedRename> FindReflectedRenames(string content, string filePath, ModuleRename moduleRename, ReflectedRedirectKind kind, Regex regex)
        {
            foreach (Match match in regex.Matches(content))
            {
                string sourceCppName = match.Groups["Name"].Value;
                string embeddedCppName = RenameReflectedCppName(sourceCppName, moduleRename.SourcePlugin.Name, moduleRename.EmbeddedPrefix, filePath);
                yield return new ReflectedRename(kind, moduleRename.SourceModuleName, moduleRename.EmbeddedModuleName, sourceCppName, embeddedCppName);
            }
        }

        /// <summary>
        /// Renames one C++ reflected type by replacing the child-prefix portion after the Unreal type prefix.
        /// </summary>
        private static string RenameReflectedCppName(string sourceCppName, string sourcePrefix, string embeddedPrefix, string filePath)
        {
            if (sourceCppName.Length < 2)
            {
                throw new InvalidOperationException($"Reflected type name '{sourceCppName}' in '{filePath}' is too short to rename safely.");
            }

            string unrealPrefix = sourceCppName.Substring(0, 1);
            string nameWithoutUnrealPrefix = sourceCppName.Substring(1);
            if (!nameWithoutUnrealPrefix.StartsWith(sourcePrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Reflected type '{sourceCppName}' in '{filePath}' must start with Unreal prefix plus '{sourcePrefix}' so Deploy Plugin can generate a co-installable embedded type name.");
            }

            return unrealPrefix + embeddedPrefix + nameWithoutUnrealPrefix.Substring(sourcePrefix.Length);
        }

        /// <summary>
        /// Builds one non-recursive replacement set so inserted embedded names are not rewritten again by later rules.
        /// </summary>
        private static IReadOnlyList<ReplacementRule> BuildReplacementRules(IReadOnlyList<ModuleRename> moduleRenames, IReadOnlyList<ReflectedRename> reflectedRenames)
        {
            Dictionary<string, string> replacementMap = new(StringComparer.Ordinal);
            foreach (ModuleRename moduleRename in moduleRenames)
            {
                AddReplacement(replacementMap, moduleRename.SourceModuleName, moduleRename.EmbeddedModuleName);
                AddReplacement(replacementMap, ToApiMacro(moduleRename.SourceModuleName), ToApiMacro(moduleRename.EmbeddedModuleName));
            }

            foreach (ReflectedRename reflectedRename in reflectedRenames)
            {
                AddReplacement(replacementMap, reflectedRename.SourceCppName, reflectedRename.EmbeddedCppName);
            }

            return replacementMap
                .Select(item => new ReplacementRule(item.Key, item.Value))
                .OrderByDescending(rule => rule.SourceValue.Length)
                .ToList();
        }

        /// <summary>
        /// Adds one replacement rule and rejects contradictory mappings for the same source token.
        /// </summary>
        private static void AddReplacement(Dictionary<string, string> replacements, string sourceValue, string embeddedValue)
        {
            if (replacements.TryGetValue(sourceValue, out string? existingValue))
            {
                if (!string.Equals(existingValue, embeddedValue, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Replacement token '{sourceValue}' maps to both '{existingValue}' and '{embeddedValue}'.");
                }

                return;
            }

            replacements.Add(sourceValue, embeddedValue);
        }

        /// <summary>
        /// Rewrites source and config file contents across the whole staged plugin using the global replacement map.
        /// </summary>
        private static void RewriteTextFiles(Plugin stagingPlugin, IReadOnlyList<ReplacementRule> replacements, ILogger logger)
        {
            foreach (string rootPath in GetRewriteRoots(stagingPlugin))
            {
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                foreach (string filePath in Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories).Where(IsTextFile))
                {
                    string content = File.ReadAllText(filePath);
                    string rewrittenContent = ApplyReplacements(content, replacements);
                    if (!string.Equals(content, rewrittenContent, StringComparison.Ordinal))
                    {
                        File.WriteAllText(filePath, rewrittenContent);
                        logger.LogInformation("Rewrote staged plugin file for merged module names: {RelativePath}", Path.GetRelativePath(stagingPlugin.PluginPath, filePath));
                    }
                }
            }
        }

        /// <summary>
        /// Renames files and directories whose path segments include source module or reflected type names.
        /// </summary>
        private static void RenamePaths(Plugin stagingPlugin, IReadOnlyList<ReplacementRule> replacements, ILogger logger)
        {
            foreach (string rootPath in GetRewriteRoots(stagingPlugin))
            {
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                foreach (string filePath in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                {
                    RenamePathIfNeeded(filePath, replacements, stagingPlugin.PluginPath, logger, isDirectory: false);
                }

                foreach (string directoryPath in Directory.EnumerateDirectories(rootPath, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                {
                    RenamePathIfNeeded(directoryPath, replacements, stagingPlugin.PluginPath, logger, isDirectory: true);
                }
            }
        }

        /// <summary>
        /// Moves one path when its file or directory name changes under the replacement map.
        /// </summary>
        private static void RenamePathIfNeeded(string path, IReadOnlyList<ReplacementRule> replacements, string pluginRootPath, ILogger logger, bool isDirectory)
        {
            string sourceName = Path.GetFileName(path);
            string embeddedName = ApplyReplacements(sourceName, replacements);
            if (string.Equals(sourceName, embeddedName, StringComparison.Ordinal))
            {
                return;
            }

            string? parentPath = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return;
            }

            string embeddedPath = Path.Combine(parentPath, embeddedName);
            if (File.Exists(embeddedPath) || Directory.Exists(embeddedPath))
            {
                throw new InvalidOperationException($"Cannot rename staged path '{path}' to '{embeddedPath}' because the destination already exists.");
            }

            if (isDirectory)
            {
                Directory.Move(path, embeddedPath);
            }
            else
            {
                File.Move(path, embeddedPath);
            }

            logger.LogInformation("Renamed staged plugin path: {SourcePath} -> {EmbeddedPath}", Path.GetRelativePath(pluginRootPath, path), Path.GetRelativePath(pluginRootPath, embeddedPath));
        }

        /// <summary>
        /// Applies all replacements in one regex pass so replacement values are never recursively rewritten.
        /// </summary>
        private static string ApplyReplacements(string value, IReadOnlyList<ReplacementRule> replacements)
        {
            if (replacements.Count == 0 || string.IsNullOrEmpty(value))
            {
                return value;
            }

            string pattern = string.Join("|", replacements.Select(rule => rule.Pattern));
            Dictionary<string, ReplacementRule> replacementsBySource = replacements.ToDictionary(rule => rule.SourceValue, StringComparer.Ordinal);
            return Regex.Replace(value, pattern, match => replacementsBySource[match.Value].EmbeddedValue);
        }

        /// <summary>
        /// Returns the staged plugin subtrees that are safe to rewrite as text and path names.
        /// </summary>
        private static IEnumerable<string> GetRewriteRoots(Plugin stagingPlugin)
        {
            yield return Path.Combine(stagingPlugin.PluginPath, "Source");
            yield return Path.Combine(stagingPlugin.PluginPath, "Config");
        }

        /// <summary>
        /// Updates the staged plugin descriptor so merged plugin modules become local modules instead of plugin dependencies.
        /// </summary>
        private static void UpdateStagedPluginDescriptor(Plugin stagingPlugin, IReadOnlyList<ResolvedMergePlugin> mergePlugins, IReadOnlyList<ModuleRename> moduleRenames)
        {
            JObject stagedDescriptor = JObject.Parse(File.ReadAllText(stagingPlugin.UPluginPath));
            JArray existingModules = stagedDescriptor["Modules"] as JArray ?? new JArray();
            JArray embeddedModules = new();
            foreach (ModuleRename moduleRename in moduleRenames)
            {
                JObject embeddedModuleDeclaration = (JObject)moduleRename.ModuleDeclaration.DeepClone();
                embeddedModuleDeclaration["Name"] = moduleRename.EmbeddedModuleName;
                embeddedModules.Add(embeddedModuleDeclaration);
            }

            stagedDescriptor["Modules"] = new JArray(embeddedModules.Concat(existingModules.Select(module => module.DeepClone())));
            UpdatePluginDependencies(stagedDescriptor, stagingPlugin.Name, mergePlugins);
            File.WriteAllText(stagingPlugin.UPluginPath, stagedDescriptor.ToString());
        }

        /// <summary>
        /// Removes merged user-plugin dependencies while carrying through any dependencies declared by the embedded plugins.
        /// </summary>
        private static void UpdatePluginDependencies(JObject stagedDescriptor, string stagingPluginName, IReadOnlyList<ResolvedMergePlugin> mergePlugins)
        {
            HashSet<string> mergedPluginNames = mergePlugins.Select(plugin => plugin.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, JObject> dependenciesByName = new(StringComparer.OrdinalIgnoreCase);

            foreach (JObject dependency in GetPluginDependencyDeclarations(stagedDescriptor))
            {
                string dependencyName = GetRequiredName(dependency, "staged plugin dependency");
                if (!mergedPluginNames.Contains(dependencyName))
                {
                    dependenciesByName[dependencyName] = (JObject)dependency.DeepClone();
                }
            }

            foreach (ResolvedMergePlugin mergePlugin in mergePlugins)
            {
                foreach (JObject dependency in GetPluginDependencyDeclarations(mergePlugin.Descriptor))
                {
                    string dependencyName = GetRequiredName(dependency, $"merge plugin '{mergePlugin.Name}' dependency");
                    if (mergedPluginNames.Contains(dependencyName) || string.Equals(dependencyName, stagingPluginName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    dependenciesByName.TryAdd(dependencyName, (JObject)dependency.DeepClone());
                }
            }

            if (dependenciesByName.Count == 0)
            {
                stagedDescriptor.Remove("Plugins");
                return;
            }

            stagedDescriptor["Plugins"] = new JArray(dependenciesByName.Values);
        }

        /// <summary>
        /// Appends merged plugin config into the host plugin's config file so module/type references use staged names.
        /// </summary>
        private static void AppendMergedConfigFiles(Plugin stagingPlugin, IReadOnlyList<ResolvedMergePlugin> mergePlugins, IReadOnlyList<ReplacementRule> replacements, ILogger logger)
        {
            List<string> mergedConfigLines = new();
            foreach (ResolvedMergePlugin mergePlugin in mergePlugins)
            {
                string configPath = Path.Combine(mergePlugin.PluginPath, "Config");
                if (!Directory.Exists(configPath))
                {
                    continue;
                }

                foreach (string configFilePath in Directory.EnumerateFiles(configPath, "*.ini", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    mergedConfigLines.Add(string.Empty);
                    mergedConfigLines.Add($"; Merged from {mergePlugin.Name}/{Path.GetRelativePath(configPath, configFilePath).Replace('\\', '/')}");
                    mergedConfigLines.AddRange(File.ReadAllLines(configFilePath).Select(line => ApplyReplacements(line, replacements)));
                }
            }

            if (mergedConfigLines.Count == 0)
            {
                return;
            }

            string stagedConfigPath = Path.Combine(stagingPlugin.PluginPath, "Config", $"Default{stagingPlugin.Name}.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedConfigPath)!);
            List<string> stagedConfigLines = File.Exists(stagedConfigPath) ? File.ReadAllLines(stagedConfigPath).ToList() : new List<string>();
            stagedConfigLines.AddRange(mergedConfigLines);
            File.WriteAllLines(stagedConfigPath, stagedConfigLines);
            logger.LogInformation("Merged config from {MergePluginCount} plugin(s) into staged plugin config: {ConfigPath}", mergePlugins.Count, stagedConfigPath);
        }

        /// <summary>
        /// Appends explicit CoreRedirect entries for reflected type renames generated during flattening.
        /// </summary>
        private static void AppendCoreRedirects(Plugin stagingPlugin, IReadOnlyList<ReflectedRename> reflectedRenames, ILogger logger)
        {
            if (reflectedRenames.Count == 0)
            {
                return;
            }

            string configPath = Path.Combine(stagingPlugin.PluginPath, "Config", $"Default{stagingPlugin.Name}.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            List<string> lines = File.Exists(configPath) ? File.ReadAllLines(configPath).ToList() : new List<string>();
            int coreRedirectSectionIndex = lines.FindIndex(line => string.Equals(line.Trim(), "[CoreRedirects]", StringComparison.Ordinal));
            if (coreRedirectSectionIndex < 0)
            {
                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                {
                    lines.Add(string.Empty);
                }

                lines.Add("[CoreRedirects]");
                coreRedirectSectionIndex = lines.Count - 1;
            }

            HashSet<string> existingLines = lines.ToHashSet(StringComparer.Ordinal);
            int insertIndex = GetSectionAppendIndex(lines, coreRedirectSectionIndex);
            foreach (string redirectLine in reflectedRenames.Select(CreateCoreRedirectLine).Distinct(StringComparer.Ordinal))
            {
                if (existingLines.Add(redirectLine))
                {
                    lines.Insert(insertIndex, redirectLine);
                    insertIndex++;
                }
            }

            File.WriteAllLines(configPath, lines);
            logger.LogInformation("Generated {RedirectCount} CoreRedirect entries for flattened reflected types: {ConfigPath}", reflectedRenames.Count, configPath);
        }

        /// <summary>
        /// Returns the insertion index at the end of one config section but before the next section header.
        /// </summary>
        private static int GetSectionAppendIndex(IReadOnlyList<string> lines, int sectionIndex)
        {
            int insertIndex = sectionIndex + 1;
            while (insertIndex < lines.Count && !lines[insertIndex].TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                insertIndex++;
            }

            return insertIndex;
        }

        /// <summary>
        /// Creates one Unreal CoreRedirect line for a reflected type that moved into an embedded module and type prefix.
        /// </summary>
        private static string CreateCoreRedirectLine(ReflectedRename reflectedRename)
        {
            string redirectKey = reflectedRename.Kind switch
            {
                ReflectedRedirectKind.Class => "ClassRedirects",
                ReflectedRedirectKind.Struct => "StructRedirects",
                ReflectedRedirectKind.Enum => "EnumRedirects",
                _ => throw new InvalidOperationException($"Unsupported reflected redirect kind: {reflectedRename.Kind}")
            };

            string sourcePathName = GetReflectedPathName(reflectedRename.Kind, reflectedRename.SourceCppName);
            string embeddedPathName = GetReflectedPathName(reflectedRename.Kind, reflectedRename.EmbeddedCppName);
            return $"+{redirectKey}=(OldName=\"/Script/{reflectedRename.SourceModuleName}.{sourcePathName}\",NewName=\"/Script/{reflectedRename.EmbeddedModuleName}.{embeddedPathName}\")";
        }

        /// <summary>
        /// Converts a C++ reflected type name into the path name Unreal uses in CoreRedirect entries.
        /// </summary>
        private static string GetReflectedPathName(ReflectedRedirectKind kind, string cppName)
        {
            return kind switch
            {
                ReflectedRedirectKind.Class when cppName.Length > 1 && (cppName[0] == 'U' || cppName[0] == 'A') => cppName.Substring(1),
                ReflectedRedirectKind.Struct when cppName.Length > 1 && cppName[0] == 'F' => cppName.Substring(1),
                _ => cppName
            };
        }

        /// <summary>
        /// Performs cheap structural checks that catch incomplete flattening before UAT emits a longer build failure.
        /// </summary>
        private static void ValidateFlattenedStaging(Plugin stagingPlugin, IReadOnlyList<ResolvedMergePlugin> mergePlugins, IReadOnlyList<ModuleRename> moduleRenames)
        {
            JObject stagedDescriptor = JObject.Parse(File.ReadAllText(stagingPlugin.UPluginPath));
            HashSet<string> mergedPluginNames = mergePlugins.Select(plugin => plugin.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (JObject dependency in GetPluginDependencyDeclarations(stagedDescriptor))
            {
                string dependencyName = GetRequiredName(dependency, "staged plugin dependency");
                if (mergedPluginNames.Contains(dependencyName))
                {
                    throw new InvalidOperationException($"Staged plugin descriptor still depends on merged plugin '{dependencyName}'.");
                }
            }

            foreach (ModuleRename moduleRename in moduleRenames)
            {
                string embeddedBuildFilePath = Path.Combine(stagingPlugin.PluginPath, "Source", moduleRename.EmbeddedModuleName, moduleRename.EmbeddedModuleName + ".Build.cs");
                if (!File.Exists(embeddedBuildFilePath))
                {
                    throw new FileNotFoundException($"Embedded module Build.cs was not created: {embeddedBuildFilePath}", embeddedBuildFilePath);
                }
            }
        }

        /// <summary>
        /// Returns all module names declared by a plugin descriptor.
        /// </summary>
        private static IEnumerable<string> GetModuleNames(JObject descriptor)
        {
            return GetModuleDeclarations(descriptor).Select(module => GetRequiredName(module, "plugin module"));
        }

        /// <summary>
        /// Returns module declarations from a descriptor while ignoring malformed non-object entries until validation reads them.
        /// </summary>
        private static IEnumerable<JObject> GetModuleDeclarations(JObject descriptor)
        {
            return (descriptor["Modules"] as JArray ?? new JArray()).OfType<JObject>();
        }

        /// <summary>
        /// Returns plugin dependency declarations from a descriptor while ignoring malformed non-object entries until validation reads them.
        /// </summary>
        private static IEnumerable<JObject> GetPluginDependencyDeclarations(JObject descriptor)
        {
            return (descriptor["Plugins"] as JArray ?? new JArray()).OfType<JObject>();
        }

        /// <summary>
        /// Reads the required Name property from a descriptor object.
        /// </summary>
        private static string GetRequiredName(JObject descriptorObject, string description)
        {
            string? name = descriptorObject["Name"]?.ToObject<string>();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException($"Missing Name in {description} declaration.");
            }

            return name;
        }

        /// <summary>
        /// Returns whether a file extension is safe to treat as text for source/config rewriting.
        /// </summary>
        private static bool IsTextFile(string filePath)
        {
            return TextFileExtensions.Contains(Path.GetExtension(filePath));
        }

        /// <summary>
        /// Converts one Unreal module name into its exported API macro name.
        /// </summary>
        private static string ToApiMacro(string moduleName)
        {
            return moduleName.ToUpperInvariant() + "_API";
        }

        /// <summary>
        /// Ensures generated C++ and module identifiers are syntactically safe before files are copied or rewritten.
        /// </summary>
        private static void ValidateIdentifier(string value, string description)
        {
            if (!IdentifierRegex.IsMatch(value))
            {
                throw new InvalidOperationException($"Invalid {description}: '{value}'.");
            }
        }

        /// <summary>
        /// Describes one text/path replacement while preventing source-prefix names from matching already-embedded names.
        /// </summary>
        private sealed class ReplacementRule
        {
            public ReplacementRule(string sourceValue, string embeddedValue)
            {
                SourceValue = sourceValue;
                EmbeddedValue = embeddedValue;
                Pattern = BuildPattern(sourceValue, embeddedValue);
            }

            // The source token found in development-time code or descriptors.
            public string SourceValue { get; }

            // The generated token used in the staged flattened plugin.
            public string EmbeddedValue { get; }

            // The regex pattern for this rule, including a guard against double-renaming embedded tokens.
            public string Pattern { get; }

            /// <summary>
            /// Builds a regex pattern that skips matches already followed by the generated suffix.
            /// </summary>
            private static string BuildPattern(string sourceValue, string embeddedValue)
            {
                string escapedSourceValue = Regex.Escape(sourceValue);
                if (!embeddedValue.StartsWith(sourceValue, StringComparison.Ordinal))
                {
                    return escapedSourceValue;
                }

                string embeddedSuffix = embeddedValue.Substring(sourceValue.Length);
                return string.IsNullOrEmpty(embeddedSuffix)
                    ? escapedSourceValue
                    : escapedSourceValue + "(?!" + Regex.Escape(embeddedSuffix) + ")";
            }
        }

        /// <summary>
        /// Describes one user-authored merge option entry.
        /// </summary>
        private sealed class MergeRule
        {
            public MergeRule(string sourcePluginSpecifier, string? embeddedPrefixOverride)
            {
                SourcePluginSpecifier = sourcePluginSpecifier;
                EmbeddedPrefixOverride = embeddedPrefixOverride;
            }

            // The user-provided plugin name or path that identifies the merge source.
            public string SourcePluginSpecifier { get; }

            // The optional destination prefix that overrides generated child-prefix-plus-host-suffix naming.
            public string? EmbeddedPrefixOverride { get; }
        }

        /// <summary>
        /// Captures one resolved merge plugin without retaining a watcher-backed Plugin object.
        /// </summary>
        private sealed class ResolvedMergePlugin
        {
            public ResolvedMergePlugin(string name, string pluginPath, string? embeddedPrefixOverride, JObject descriptor)
            {
                Name = name;
                PluginPath = pluginPath;
                EmbeddedPrefixOverride = embeddedPrefixOverride;
                Descriptor = descriptor;
            }

            // The source plugin name that forms the reflected and module rename source prefix.
            public string Name { get; }

            // The resolved plugin directory copied into the workspace.
            public string PluginPath { get; }

            // The optional embedded prefix supplied by the deployment option entry.
            public string? EmbeddedPrefixOverride { get; }

            // The parsed source plugin descriptor used to import modules and dependencies.
            public JObject Descriptor { get; }
        }

        /// <summary>
        /// Describes one source module and its generated embedded module identity.
        /// </summary>
        private sealed class ModuleRename
        {
            public ModuleRename(ResolvedMergePlugin sourcePlugin, JObject moduleDeclaration, string sourceModuleName, string embeddedModuleName, string embeddedPrefix)
            {
                SourcePlugin = sourcePlugin;
                ModuleDeclaration = moduleDeclaration;
                SourceModuleName = sourceModuleName;
                EmbeddedModuleName = embeddedModuleName;
                EmbeddedPrefix = embeddedPrefix;
            }

            // The resolved merge plugin that owns the source module.
            public ResolvedMergePlugin SourcePlugin { get; }

            // The original module declaration to clone into the staged plugin descriptor.
            public JObject ModuleDeclaration { get; }

            // The original Unreal module name in the merge plugin.
            public string SourceModuleName { get; }

            // The generated Unreal module name used inside the staged plugin.
            public string EmbeddedModuleName { get; }

            // The generated reflected type prefix used for types from this merge plugin.
            public string EmbeddedPrefix { get; }

            // The original source module directory in the resolved merge plugin.
            public string SourceModulePath => Path.Combine(SourcePlugin.PluginPath, "Source", SourceModuleName);
        }

        /// <summary>
        /// Describes one reflected C++ type rename and its source/new script module ownership.
        /// </summary>
        private sealed class ReflectedRename
        {
            public ReflectedRename(ReflectedRedirectKind kind, string sourceModuleName, string embeddedModuleName, string sourceCppName, string embeddedCppName)
            {
                Kind = kind;
                SourceModuleName = sourceModuleName;
                EmbeddedModuleName = embeddedModuleName;
                SourceCppName = sourceCppName;
                EmbeddedCppName = embeddedCppName;
            }

            // The Unreal redirect family needed for this reflected declaration.
            public ReflectedRedirectKind Kind { get; }

            // The original script module that owns the reflected type.
            public string SourceModuleName { get; }

            // The embedded script module that owns the renamed reflected type.
            public string EmbeddedModuleName { get; }

            // The original C++ reflected type name.
            public string SourceCppName { get; }

            // The generated C++ reflected type name.
            public string EmbeddedCppName { get; }
        }

        /// <summary>
        /// Identifies which CoreRedirect array receives a reflected type rename.
        /// </summary>
        private enum ReflectedRedirectKind
        {
            Class,
            Struct,
            Enum
        }
    }
}
