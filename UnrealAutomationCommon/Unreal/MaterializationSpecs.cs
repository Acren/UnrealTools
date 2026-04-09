using System;
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
        /// Callers opt into additional filesystem categories explicitly so the spec stays generic and filesystem-shaped
        /// rather than encoding deploy-pipeline semantics.
        /// </summary>
        public static FileMaterializationSpec CreateProject(Project project, bool includePlugins = false, bool includeBuildOutputs = false)
        {
            FileMaterializationSpec spec = new()
            {
                { Path.GetFileName(project.UProjectPath), true },
                { "Config", true },
                { "Source" },
                { "Content" },
                { Path.GetFileNameWithoutExtension(project.UProjectPath) + ".png" }
            };

            if (includePlugins)
            {
                AddProjectPluginEntries(project, spec);
            }

            if (includeBuildOutputs)
            {
                spec.Add("Binaries");
                spec.Add("Build");
            }

            return spec;
        }

        /// <summary>
        /// Creates the explicit plugin subset used for staging, workspace materialization, and source archives. The spec
        /// preserves source and packaged content while excluding generated build products such as Binaries and
        /// Intermediate.
        /// </summary>
        public static FileMaterializationSpec CreatePlugin(Plugin plugin)
        {
            if (plugin == null)
            {
                throw new ArgumentNullException(nameof(plugin));
            }

            return CreatePlugin(plugin.PluginPath);
        }

        /// <summary>
        /// Creates the explicit plugin subset for one plugin directory without constructing a long-lived watcher-backed
        /// plugin target object.
        /// </summary>
        public static FileMaterializationSpec CreatePlugin(string pluginDirectoryPath)
        {
            if (pluginDirectoryPath == null)
            {
                throw new ArgumentNullException(nameof(pluginDirectoryPath));
            }

            string pluginDescriptorFileName = Path.GetFileName(PluginPaths.Instance.FindRequiredTargetFile(pluginDirectoryPath));
            return new FileMaterializationSpec
            {
                { pluginDescriptorFileName, true },
                { "Source" },
                { "Resources" },
                { "Content" },
                { "Config" },
                { "Extras" }
            };
        }

        /// <summary>
        /// Expands the project Plugins tree into explicit plugin-subset entries so variant materialization skips each
        /// plugin's generated Intermediate folders instead of copying and deleting them later.
        /// </summary>
        private static void AddProjectPluginEntries(Project project, FileMaterializationSpec spec)
        {
            if (!Directory.Exists(project.PluginsPath))
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
                string relativePluginDirectoryPath = Path.GetRelativePath(project.PluginsPath, pluginDirectoryPath);
                spec.AddSubtree(Path.Combine("Plugins", relativePluginDirectoryPath), CreatePlugin(pluginDirectoryPath));
            }
        }
    }
}
