using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public class PluginUtils
    {
        public static bool IsPluginFile(string FilePath)
        {
            return Path.GetExtension(FilePath).Equals(".uplugin", System.StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
