using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Centralizes reusable project and plugin materialization shapes so operations do not duplicate explicit filesystem
    /// copy-entry lists.
    /// </summary>
    internal static class MaterializationSpecs
    {
        /// <summary>
        /// Creates the explicit project subset copied into isolated workspaces and example projects. The spec keeps only
        /// author-maintained project inputs and leaves generated output folders behind.
        /// </summary>
        public static FileMaterializationSpec CreateProject(Project project)
        {
            return new FileMaterializationSpec
            {
                { Path.GetFileName(project.UProjectPath), true },
                { "Config", true },
                { "Source" },
                { "Content" },
                { Path.GetFileNameWithoutExtension(project.UProjectPath) + ".png" }
            };
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
