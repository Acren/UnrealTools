using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalAutomation.Core.IO;

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
        /// Plugin inclusion is name-driven so callers decide policy while this spec owns project-tree materialization.
        /// </summary>
        public static FileMaterializationSpec CreateProject(Project project, IReadOnlySet<string>? includedPluginNames = null, bool includeBuildOutputs = false)
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
                AddProjectPluginEntries(project, spec, includedPluginNames, includeBuildOutputs);
            }

            if (includeBuildOutputs)
            {
                spec.Add("Binaries");
                spec.Add("Build");
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
        private static void AddProjectPluginEntries(Project project, FileMaterializationSpec spec, IReadOnlySet<string> includedPluginNames, bool includeBuildOutputs)
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
                spec.AddSubtree(Path.Combine("Plugins", relativePluginDirectoryPath), CreatePlugin(pluginDirectoryPath, includeBuildOutputs));
            }
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
