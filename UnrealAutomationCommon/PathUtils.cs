using System.IO;

namespace UnrealAutomationCommon
{
    public static class PathUtils
    {
        public static bool IsSubPathOf(this string subPath, string basePath)
        {
            var rel = Path.GetRelativePath(
                basePath.Replace('\\', '/'),
                subPath.Replace('\\', '/'));
            return rel != "."
                   && rel != ".."
                   && !rel.StartsWith("../")
                   && !Path.IsPathRooted(rel);
        }
    }
}
