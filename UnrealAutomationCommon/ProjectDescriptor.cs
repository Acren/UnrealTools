using Newtonsoft.Json;
using System.IO;

namespace UnrealAutomationCommon
{
    public class ProjectDescriptor
    {
        public string EngineAssociation { get; set; }

        public string EngineFriendlyName => IsEngineInstalled() ? EngineAssociation : GetEngineInstallDirectory();

        public static ProjectDescriptor Load(string uProjectPath)
        {
            return JsonConvert.DeserializeObject<ProjectDescriptor>(File.ReadAllText(uProjectPath));
        }

        public string GetEngineInstallDirectory()
        {
            return ProjectUtils.GetEngineInstallDirectory(EngineAssociation);
        }

        public bool IsEngineInstalled()
        {
            return !EngineAssociation.StartsWith("{");
        }

        public string GetRunUATPath()
        {
            return EnginePaths.GetRunUATPath(GetEngineInstallDirectory());
        }

        public string GetBuildPath()
        {
            return EnginePaths.GetBuildPath(GetEngineInstallDirectory());
        }
    }
}
