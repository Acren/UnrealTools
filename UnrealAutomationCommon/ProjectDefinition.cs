using Newtonsoft.Json;
using System.IO;

namespace UnrealAutomationCommon
{
    public class ProjectDefinition
    {
        public string EngineAssociation { get; set; }

        public static bool IsProjectFile(string FilePath)
        {
            return Path.GetExtension(FilePath).Equals(".uproject", System.StringComparison.InvariantCultureIgnoreCase);
        }

        public static ProjectDefinition Load(string uProjectPath)
        {
            return JsonConvert.DeserializeObject<ProjectDefinition>(File.ReadAllText(uProjectPath));
        }

        public string GetEngineInstallDirectory()
        {
            return ProjectUtils.GetEngineInstallDirectory(EngineAssociation);
        }

        public string GetRunUAT()
        {
            return Path.Combine(GetEngineInstallDirectory(), "Engine", "Build", "BatchFiles", "RunUAT.bat");
        }
    }
}
