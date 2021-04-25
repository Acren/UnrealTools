using System.IO;

namespace UnrealAutomationCommon
{
    public class PluginUtils
    {
        public static bool IsPluginFile(string FilePath)
        {
            return Path.GetExtension(FilePath).Equals(".uplugin", System.StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
