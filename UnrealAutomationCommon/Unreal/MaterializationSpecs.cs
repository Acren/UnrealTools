using System.IO;
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
                spec.Add("Plugins");
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
            return new FileMaterializationSpec
            {
                { Path.GetFileName(plugin.UPluginPath), true },
                { "Source" },
                { "Resources" },
                { "Content" },
                { "Config" },
                { "Extras" }
            };
        }
    }
}
