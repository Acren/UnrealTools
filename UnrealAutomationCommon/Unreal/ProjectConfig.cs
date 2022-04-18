using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public static class ProjectConfig
    {
        public static string GetCopyrightNotice(this Project project)
        {
            string hostProjectDefaultGameConfig = Path.Combine(project.TargetDirectory, "Config", "DefaultGame.ini");
            UnrealConfig config = new(hostProjectDefaultGameConfig);
            string copyrightNotice = config.GetSection("/Script/EngineSettings.GeneralProjectSettings").GetValue("CopyrightNotice");
            return copyrightNotice;
        }
    }
}
