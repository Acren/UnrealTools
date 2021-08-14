using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public class ProjectUtils
    {
        public static bool IsProjectFile(string FilePath)
        {
            return Path.GetExtension(FilePath).Equals(".uproject", System.StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
