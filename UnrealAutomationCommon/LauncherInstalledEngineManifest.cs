using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace UnrealAutomationCommon
{
    public class LauncherInstalledEngineManifest
    {
        public List<EngineInstallation> InstallationList;

        public static string ManifestPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic\\UnrealEngineLauncher\\LauncherInstalled.dat");

        public static LauncherInstalledEngineManifest Load()
        {
            return JsonConvert.DeserializeObject<LauncherInstalledEngineManifest>(File.ReadAllText(ManifestPath));
        }
    }

    public class EngineInstallation
    {
        public string InstallLocation;
        public string AppName;
    }
}
