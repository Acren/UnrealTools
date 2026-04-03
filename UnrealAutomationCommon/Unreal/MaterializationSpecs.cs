using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Centralizes reusable project and plugin materialization shapes so operations do not duplicate explicit filesystem
    /// copy-entry lists.
    /// </summary>
    internal static class MaterializationSpecs
    {
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
