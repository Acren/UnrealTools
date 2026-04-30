using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalAutomation.Core.IO;
using Newtonsoft.Json.Linq;

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
        /// Project and plugin build outputs are controlled separately so persistent project workspaces keep their own root
        /// executable cache while still being able to inherit prebuilt plugin modules.
        /// </summary>
        public static FileMaterializationSpec CreateProject(
            Project project,
            IReadOnlySet<string>? includedPluginNames = null,
            bool includeProjectBuildOutputs = false,
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
                AddProjectPluginEntries(project, spec, includedPluginNames, includePluginBuildOutputs);
            }

            if (includeProjectBuildOutputs)
            {
                // Root project build outputs belong to the destination project workspace cache, not necessarily to every
                // project tree that needs prebuilt plugin modules.
                spec.Add("Binaries");
                spec.Add("Build");
            }
            else if (includeProjectEditorBuildOutputs)
            {
                // Editor receipts are required by BuildCookRun when editor compilation is disabled, but game executable
                // outputs remain owned by each destination project workspace so persistent variant caches stay isolated.
                AddProjectEditorBuildOutputEntries(project, spec);
            }

            return spec;
        }

        /// <summary>
        /// Creates the generated-output subset copied from a stable cached build workspace back into the session project
        /// that package, launch, and archive steps continue to read from.
        /// </summary>
        public static FileMaterializationSpec CreateProjectBuildOutputs(Project project)
        {
            FileMaterializationSpec spec = new()
            {
                { "Binaries" },
                { "Build" }
            };

            AddProjectPluginBuildOutputEntries(project, spec);
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
        /// Reads plugin directory paths relative to the project Plugins root so cache identity and stale-plugin cleanup use
        /// the filesystem location Unreal will scan, not only the plugin display name.
        /// </summary>
        public static IReadOnlySet<string> GetProjectPluginRelativePaths(Project project)
        {
            return GetProjectPluginDirectories(project)
                .Select(pluginDirectoryPath => Path.GetRelativePath(project.PluginsPath, pluginDirectoryPath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads project plugin module declarations as stable shape strings so structural plugin changes receive a fresh
        /// build cache while ordinary source edits keep using the warm cache for the same module layout.
        /// </summary>
        public static IReadOnlySet<string> GetProjectPluginModuleShape(Project project)
        {
            return GetProjectPluginDirectories(project)
                .SelectMany(pluginDirectoryPath => GetPluginModuleShape(project, pluginDirectoryPath))
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
        /// Expands the project Plugins tree into explicit plugin-subset entries so variant materialization skips each
        /// plugin's generated Intermediate folders instead of copying and deleting them later.
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
                spec.AddSubtree(Path.Combine("Plugins", relativePluginDirectoryPath), CreatePlugin(pluginDirectoryPath, includePluginBuildOutputs));
            }
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
                JObject receipt = JObject.Parse(File.ReadAllText(receiptPath));
                if (!string.Equals(receipt.Value<string>("TargetType"), "Editor", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relativePaths.Add(Path.GetRelativePath(project.ProjectPath, receiptPath));
                foreach (string receiptProductPath in EnumerateReceiptProductPaths(receipt))
                {
                    if (TryResolveProjectBinariesPath(project.ProjectPath, projectBinariesPath, receiptProductPath, out string relativePath))
                    {
                        relativePaths.Add(relativePath);
                    }
                }
            }

            foreach (string relativePath in relativePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                spec.Add(relativePath);
            }
        }

        /// <summary>
        /// Reads file paths from the receipt sections that declare the products UBT created and runtime files UAT may need.
        /// </summary>
        private static IEnumerable<string> EnumerateReceiptProductPaths(JObject receipt)
        {
            foreach (string propertyName in new[] { "BuildProducts", "RuntimeDependencies" })
            {
                if (receipt[propertyName] is not JArray entries)
                {
                    continue;
                }

                foreach (JToken entry in entries)
                {
                    string? path = entry.Type == JTokenType.String
                        ? entry.Value<string>()
                        : entry.Value<string>("Path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        yield return path;
                    }
                }
            }
        }

        /// <summary>
        /// Resolves one receipt path and accepts only files under the project's root Binaries directory.
        /// </summary>
        private static bool TryResolveProjectBinariesPath(string projectPath, string projectBinariesPath, string receiptPath, out string relativePath)
        {
            relativePath = string.Empty;
            string? absolutePath = ResolveReceiptPath(projectPath, receiptPath);
            if (absolutePath == null || !IsSameOrDescendantPath(projectBinariesPath, absolutePath))
            {
                return false;
            }

            relativePath = Path.GetRelativePath(projectPath, absolutePath);
            return true;
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
        /// Adds generated-output entries for every plugin currently present in a project workspace so cached build products
        /// for project plugins are copied back alongside the project's own binaries.
        /// </summary>
        private static void AddProjectPluginBuildOutputEntries(Project project, FileMaterializationSpec spec)
        {
            if (!Directory.Exists(project.PluginsPath))
            {
                return;
            }

            /* Discover plugins recursively so grouped plugin folders copy their generated output from the matching nested
               location without copying source, content, or Intermediate folders. */
            foreach (string pluginDirectoryPath in GetProjectPluginDirectories(project)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string relativePluginDirectoryPath = Path.GetRelativePath(project.PluginsPath, pluginDirectoryPath);
                spec.Add(Path.Combine("Plugins", relativePluginDirectoryPath, "Binaries"));
                spec.Add(Path.Combine("Plugins", relativePluginDirectoryPath, "Build"));
            }
        }

        /// <summary>
        /// Enumerates valid plugin directories beneath a project Plugins root while tolerating projects without plugins.
        /// </summary>
        private static IEnumerable<string> GetProjectPluginDirectories(Project project)
        {
            if (!Directory.Exists(project.PluginsPath))
            {
                return Enumerable.Empty<string>();
            }

            return Directory.GetDirectories(project.PluginsPath, "*", SearchOption.AllDirectories)
                .Where(PluginPaths.Instance.IsTargetDirectory);
        }

        /// <summary>
        /// Builds module-shape entries for one plugin without creating a watcher-backed Plugin target.
        /// </summary>
        private static IEnumerable<string> GetPluginModuleShape(Project project, string pluginDirectoryPath)
        {
            string relativePluginPath = Path.GetRelativePath(project.PluginsPath, pluginDirectoryPath);
            string pluginDescriptorPath = PluginPaths.Instance.FindRequiredTargetFile(pluginDirectoryPath);
            PluginDescriptor pluginDescriptor = PluginDescriptor.Load(pluginDescriptorPath);
            return pluginDescriptor.Modules
                .Select(module => $"{relativePluginPath}:Module:{module.Name}:{module.Type}");
        }
    }
}
