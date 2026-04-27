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
            foreach (string pluginDirectoryPath in Directory.GetDirectories(project.PluginsPath, "*", SearchOption.AllDirectories)
                         .Where(PluginPaths.Instance.IsTargetDirectory)
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
    }
}
