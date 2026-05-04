using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalAutomation.Core.IO;
using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Centralizes reusable project and plugin materialization shapes so operations do not duplicate explicit filesystem
    /// copy-entry lists.
    /// </summary>
    internal static class MaterializationSpecs
    {
        /// <summary>
        /// Creates the explicit project subset copied into isolated workspaces, example projects, and prepared variants.
        /// Editor and plugin build outputs are controlled separately so persistent project workspaces keep their own root
        /// executable cache while prepared variants can still inherit the built artifacts they need.
        /// </summary>
        public static FileMaterializationSpec CreateProject(
            Project project,
            IReadOnlySet<string>? includedPluginNames = null,
            bool includeProjectEditorBuildOutputs = false,
            bool includePluginBuildOutputs = false)
        {
            FileMaterializationSpec spec = new()
            {
                { Path.GetFileName(project.UProjectPath), true },
                { "Config", true },
                { "Source" },
                { "Content" },
                { Path.GetFileNameWithoutExtension(project.UProjectPath) + ".png" }
            };

            if (includedPluginNames != null)
            {
                // The project Plugins directory is an explicit materialization scope: selected plugins and preserved
                // generated outputs may remain, while stale plugin roots from earlier workspace uses are pruned.
                spec.Sync("Plugins");
                AddProjectPluginEntries(project, spec, includedPluginNames, includePluginBuildOutputs);
            }

            if (includeProjectEditorBuildOutputs)
            {
                // Editor receipts are required by BuildCookRun when editor compilation is disabled, but game executable
                // outputs remain owned by each destination project workspace so persistent variant caches stay isolated.
                AddProjectEditorBuildOutputEntries(project, spec);
            }

            return spec;
        }

        /// <summary>
        /// Creates the generated-output subset declared by one current target receipt in a cached project workspace.
        /// </summary>
        public static FileMaterializationSpec CreateProjectBuildOutputs(Project project, ProjectTargetBuildSpec buildTarget)
        {
            _ = project ?? throw new ArgumentNullException(nameof(project));
            string receiptPath = buildTarget.GetReceiptPath(project);
            BuildReceipt receipt = ReadBuildReceipt(receiptPath);
            ValidateBuildReceipt(receipt, receiptPath, buildTarget);

            // The target receipt is itself required by package-only BuildCookRun, and every project-local receipt output is
            // copied by exact path so stale products from a warm workspace cannot leak into the packaged project.
            HashSet<string> relativePaths = new(StringComparer.OrdinalIgnoreCase);
            AddRequiredProjectRelativePath(relativePaths, project.ProjectPath, Path.GetRelativePath(project.ProjectPath, receiptPath), receiptPath);
            foreach (string receiptOutputPath in receipt.EnumerateOutputPaths())
            {
                if (TryResolveProjectRelativePath(project.ProjectPath, receiptOutputPath, out string relativePath))
                {
                    AddRequiredProjectRelativePath(relativePaths, project.ProjectPath, relativePath, receiptPath);
                }
            }

            FileMaterializationSpec spec = new();
            foreach (string relativePath in relativePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                spec.Add(relativePath, required: true);
            }

            return spec;
        }

        /// <summary>
        /// Reads plugin names from a project Plugins tree without constructing watcher-backed Plugin targets, allowing
        /// materialization callers to preserve the current project plugin set cheaply.
        /// </summary>
        public static IReadOnlySet<string> GetProjectPluginNames(Project project)
        {
            return GetProjectPluginDirectories(project)
                .Select(Path.GetFileName)
                .Where(pluginName => !string.IsNullOrWhiteSpace(pluginName))
                .Select(pluginName => pluginName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates the explicit plugin subset used for staging, workspace materialization, and source archives. The
        /// default source-style subset preserves authored plugin payloads while excluding generated outputs.
        /// </summary>
        public static FileMaterializationSpec CreatePlugin(Plugin plugin, bool includeBuildOutputs = false)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            return CreatePlugin(plugin.PluginPath, includeBuildOutputs);
        }

        /// <summary>
        /// Creates the explicit plugin subset for one plugin directory without constructing a long-lived watcher-backed
        /// plugin target object.
        /// </summary>
        public static FileMaterializationSpec CreatePlugin(string pluginDirectoryPath, bool includeBuildOutputs = false)
        {
            if (pluginDirectoryPath == null)
            {
                throw new ArgumentNullException(nameof(pluginDirectoryPath));
            }

            string pluginDescriptorFileName = Path.GetFileName(PluginPaths.Instance.FindRequiredTargetFile(pluginDirectoryPath));
            FileMaterializationSpec spec = new()
            {
                { pluginDescriptorFileName, true },
                { "Source" },
                { "Resources" },
                { "Content" },
                { "Config" },
                { "Extras" }
            };

            if (includeBuildOutputs)
            {
                // Prebuilt project variants run packaging with editor compilation disabled, so each enabled code plugin
                // must carry its already-built module binaries alongside its descriptor and authored content.
                spec.Add("Binaries");
                spec.Add("Build");
            }

            return spec;
        }

        /// <summary>
        /// Expands the project Plugins tree into explicit plugin-subset entries and preserve rules so synchronized plugin
        /// materialization removes omitted plugins without discarding generated outputs for included plugins.
        /// </summary>
        private static void AddProjectPluginEntries(Project project, FileMaterializationSpec spec, IReadOnlySet<string> includedPluginNames, bool includePluginBuildOutputs)
        {
            if (!Directory.Exists(project.PluginsPath))
            {
                return;
            }

            if (includedPluginNames.Count == 0)
            {
                return;
            }

            /* Preserve files that live directly under the project Plugins root while switching plugin directories to
               explicit subset copies. */
            foreach (string pluginsRootFilePath in Directory.GetFiles(project.PluginsPath).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                spec.Add(Path.Combine("Plugins", Path.GetFileName(pluginsRootFilePath)));
            }

            /* Discover plugins recursively so grouped plugin folders still materialize through explicit per-plugin
               subsets rather than one broad recursive Plugins copy. */
            foreach (string pluginDirectoryPath in GetProjectPluginDirectories(project)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string pluginName = Path.GetFileName(pluginDirectoryPath);
                if (!includedPluginNames.Contains(pluginName))
                {
                    continue;
                }

                string relativePluginDirectoryPath = Path.GetRelativePath(project.PluginsPath, pluginDirectoryPath);
                string relativeMaterializedPluginPath = Path.Combine("Plugins", relativePluginDirectoryPath);
                spec.AddSubtree(relativeMaterializedPluginPath, CreatePlugin(pluginDirectoryPath, includePluginBuildOutputs));
                PreservePluginGeneratedOutputs(spec, relativeMaterializedPluginPath, includePluginBuildOutputs);
            }
        }

        /// <summary>
        /// Preserves generated plugin output folders that are owned by Unreal builds rather than source materialization.
        /// </summary>
        private static void PreservePluginGeneratedOutputs(FileMaterializationSpec spec, string relativePluginDirectoryPath, bool includePluginBuildOutputs)
        {
            // Intermediate stays workspace-local even when built plugin binaries are copied from another prepared project.
            spec.Preserve(Path.Combine(relativePluginDirectoryPath, "Intermediate"));
            if (includePluginBuildOutputs)
            {
                return;
            }

            // When build outputs are not part of the copied input set, preserve the destination workspace's warm cache.
            spec.Preserve(Path.Combine(relativePluginDirectoryPath, "Binaries"));
            spec.Preserve(Path.Combine(relativePluginDirectoryPath, "Build"));
        }

        /// <summary>
        /// Adds only the root project binaries declared by editor target receipts so cook/package steps can find the
        /// prebuilt editor target without importing another workspace's game executable outputs.
        /// </summary>
        private static void AddProjectEditorBuildOutputEntries(Project project, FileMaterializationSpec spec)
        {
            string projectBinariesPath = Path.Combine(project.ProjectPath, "Binaries");
            if (!Directory.Exists(projectBinariesPath))
            {
                return;
            }

            // Collect entries first so multiple editor receipts cannot schedule the same file for concurrent copies.
            HashSet<string> relativePaths = new(StringComparer.OrdinalIgnoreCase);
            foreach (string receiptPath in Directory.GetFiles(projectBinariesPath, "*.target", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                BuildReceipt receipt = ReadBuildReceipt(receiptPath);
                if (!string.Equals(receipt.TargetType, "Editor", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddRequiredProjectRelativePath(relativePaths, project.ProjectPath, Path.GetRelativePath(project.ProjectPath, receiptPath), receiptPath);
                foreach (string receiptOutputPath in receipt.EnumerateOutputPaths())
                {
                    if (TryResolveProjectRelativePath(project.ProjectPath, receiptOutputPath, out string relativePath)
                        && IsSameOrDescendantPath(projectBinariesPath, Path.Combine(project.ProjectPath, relativePath)))
                    {
                        AddRequiredProjectRelativePath(relativePaths, project.ProjectPath, relativePath, receiptPath);
                    }
                }
            }

            foreach (string relativePath in relativePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                spec.Add(relativePath, required: true);
            }
        }

        /// <summary>
        /// Reads and deserializes one UBT target receipt from disk.
        /// </summary>
        private static BuildReceipt ReadBuildReceipt(string receiptPath)
        {
            if (!File.Exists(receiptPath))
            {
                throw new FileNotFoundException($"Required target receipt is missing: {receiptPath}", receiptPath);
            }

            return JsonConvert.DeserializeObject<BuildReceipt>(File.ReadAllText(receiptPath))
                ?? throw new InvalidDataException($"Could not deserialize target receipt: {receiptPath}");
        }

        /// <summary>
        /// Ensures one target receipt describes the build output that the caller requested.
        /// </summary>
        private static void ValidateBuildReceipt(BuildReceipt receipt, string receiptPath, ProjectTargetBuildSpec buildTarget)
        {
            if (!string.Equals(receipt.TargetName, buildTarget.TargetName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(receipt.Platform, buildTarget.Platform, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(receipt.Configuration, buildTarget.Configuration.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Target receipt does not match requested output {buildTarget.TargetName} {buildTarget.Platform} {buildTarget.Configuration}: {receiptPath}");
            }
        }

        /// <summary>
        /// Resolves one receipt path and accepts only paths that live under the project root.
        /// </summary>
        private static bool TryResolveProjectRelativePath(string projectPath, string receiptOutputPath, out string relativePath)
        {
            relativePath = string.Empty;
            string? absolutePath = ResolveReceiptPath(projectPath, receiptOutputPath);
            if (absolutePath == null || !IsSameOrDescendantPath(projectPath, absolutePath))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(projectPath, absolutePath);
            if (relativePath == ".")
            {
                throw new InvalidDataException($"Target receipt output cannot refer to the project root: {receiptOutputPath}");
            }

            return true;
        }

        /// <summary>
        /// Adds one project-relative output path after confirming the current receipt still points at existing content.
        /// </summary>
        private static void AddRequiredProjectRelativePath(ISet<string> relativePaths, string projectPath, string relativePath, string receiptPath)
        {
            if (relativePath == ".")
            {
                throw new InvalidDataException($"Target receipt output cannot refer to the project root: {receiptPath}");
            }

            string absolutePath = Path.GetFullPath(Path.Combine(projectPath, relativePath));
            if (!IsSameOrDescendantPath(projectPath, absolutePath))
            {
                throw new InvalidDataException($"Target receipt output must stay inside the project root: {relativePath}");
            }

            if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            {
                throw new FileNotFoundException($"Target receipt '{receiptPath}' lists a project-local output that does not exist: {absolutePath}", absolutePath);
            }

            relativePaths.Add(Path.GetRelativePath(projectPath, absolutePath));
        }

        /// <summary>
        /// Expands the receipt variables needed for project-local build products and rejects engine or unknown locations.
        /// </summary>
        private static string? ResolveReceiptPath(string projectPath, string receiptPath)
        {
            const string projectDirToken = "$(ProjectDir)";
            if (receiptPath.StartsWith(projectDirToken, StringComparison.OrdinalIgnoreCase))
            {
                string relativePath = receiptPath[projectDirToken.Length..].TrimStart('/', '\\');
                return Path.GetFullPath(Path.Combine(projectPath, relativePath));
            }

            if (Path.IsPathRooted(receiptPath))
            {
                return Path.GetFullPath(receiptPath);
            }

            return null;
        }

        /// <summary>
        /// Returns whether a resolved receipt path is the requested root itself or a descendant of that root.
        /// </summary>
        private static bool IsSameOrDescendantPath(string rootPath, string candidatePath)
        {
            string normalizedRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
            string normalizedCandidatePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
            string rootWithSeparator = normalizedRootPath + Path.DirectorySeparatorChar;
            return string.Equals(normalizedRootPath, normalizedCandidatePath, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidatePath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Represents the subset of a UBT target receipt needed for exact generated-output materialization.
        /// </summary>
        private sealed class BuildReceipt
        {
            /// <summary>
            /// Gets or sets the target name that produced the receipt.
            /// </summary>
            public string TargetName { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the Unreal target type, such as Editor or Game.
            /// </summary>
            public string TargetType { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the platform that produced the receipt.
            /// </summary>
            public string Platform { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the configuration that produced the receipt.
            /// </summary>
            public string Configuration { get; set; } = string.Empty;

            /// <summary>
            /// Gets or sets the files UBT declared as direct build products.
            /// </summary>
            public List<BuildReceiptPathEntry>? BuildProducts { get; set; }

            /// <summary>
            /// Gets or sets the files UAT may need at runtime when staging the target.
            /// </summary>
            public List<BuildReceiptPathEntry>? RuntimeDependencies { get; set; }

            /// <summary>
            /// Enumerates every non-empty output path declared by product and runtime-dependency sections.
            /// </summary>
            public IEnumerable<string> EnumerateOutputPaths()
            {
                IEnumerable<BuildReceiptPathEntry> buildProducts = BuildProducts ?? Enumerable.Empty<BuildReceiptPathEntry>();
                IEnumerable<BuildReceiptPathEntry> runtimeDependencies = RuntimeDependencies ?? Enumerable.Empty<BuildReceiptPathEntry>();
                foreach (BuildReceiptPathEntry entry in buildProducts.Concat(runtimeDependencies))
                {
                    if (!string.IsNullOrWhiteSpace(entry.Path))
                    {
                        yield return entry.Path;
                    }
                }
            }
        }

        /// <summary>
        /// Represents one path-bearing entry inside a UBT target receipt.
        /// </summary>
        private sealed class BuildReceiptPathEntry
        {
            /// <summary>
            /// Gets or sets the path string exactly as serialized by UBT.
            /// </summary>
            public string Path { get; set; } = string.Empty;
        }

        /// <summary>
        /// Enumerates valid plugin directories beneath a project Plugins root while tolerating projects without plugins.
        /// </summary>
        private static IEnumerable<string> GetProjectPluginDirectories(Project project)
        {
            return GetProjectPluginDirectories(project.PluginsPath);
        }

        /// <summary>
        /// Enumerates valid plugin directories beneath a Plugins root path without requiring a live project target.
        /// </summary>
        private static IEnumerable<string> GetProjectPluginDirectories(string pluginsPath)
        {
            if (!Directory.Exists(pluginsPath))
            {
                return Enumerable.Empty<string>();
            }

            return Directory.GetDirectories(pluginsPath, "*", SearchOption.AllDirectories)
                .Where(PluginPaths.Instance.IsTargetDirectory);
        }
    }
}
