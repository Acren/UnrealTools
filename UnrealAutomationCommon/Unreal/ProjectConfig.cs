﻿using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public static class ProjectConfig
    {
        public static string GetCopyrightNotice(this Project project)
        {
            string hostProjectDefaultGameConfig = Path.Combine(project.TargetDirectory, "Config", "DefaultGame.ini");
            UnrealConfig config = new(hostProjectDefaultGameConfig);
            ConfigSection section = config.GetSection("/Script/EngineSettings.GeneralProjectSettings");
            if (section == null)
            {
                return null;
            }
            string copyrightNotice = section.GetValue("CopyrightNotice");
            return copyrightNotice;
        }
    }
}
