using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    public class LauncherInstalledEngineManifest
    {
        public static string ManifestPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic\\UnrealEngineLauncher\\LauncherInstalled.dat");
        public List<LauncherManifestAppInstallation> InstallationList { get; set; }

        public static LauncherInstalledEngineManifest Load()
        {
            return JsonConvert.DeserializeObject<LauncherInstalledEngineManifest>(File.ReadAllText(ManifestPath));
        }
    }

    public class LauncherManifestAppInstallation
    {
        public string AppName;
        public string InstallLocation;
    }
}