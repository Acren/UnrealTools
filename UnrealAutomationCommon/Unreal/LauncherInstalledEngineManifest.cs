using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace UnrealAutomationCommon.Unreal
{
    public class LauncherInstalledEngineManifest
    {
        public List<LauncherManifestAppInstallation> InstallationList { get; set; }

        public static string ManifestPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic\\UnrealEngineLauncher\\LauncherInstalled.dat");

        public static LauncherInstalledEngineManifest Load()
        {
            return JsonConvert.DeserializeObject<LauncherInstalledEngineManifest>(File.ReadAllText(ManifestPath));
        }
    }

    public class LauncherManifestAppInstallation
    {
        public string InstallLocation;
        public string AppName;
    }
}
