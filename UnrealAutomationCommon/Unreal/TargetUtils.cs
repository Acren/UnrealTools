using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public static class TargetUtils
    {
        public static bool IsProjectFile(string path)
        {
            return File.Exists(path) && Path.GetExtension(path).Equals(".uproject", System.StringComparison.InvariantCultureIgnoreCase);
        }

        public static bool IsPluginFile(string path)
        {
            return File.Exists(path) && Path.GetExtension(path).Equals(".uplugin", System.StringComparison.InvariantCultureIgnoreCase);
        }

        public static bool IsPackageFile(string path)
        {
            return File.Exists(path) && Path.GetExtension(path).Equals(".exe", System.StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
