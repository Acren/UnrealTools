using System;
using Microsoft.Win32;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace UnrealAutomationCommon
{
    public static class Rider
    {
        private class ToolboxSettingsFile
        {
            [JsonProperty(PropertyName = "install_location")]
            public string InstallLocation;
        }

        public static string FindPath()
        {
            string ToolboxPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JetBrains\\Toolbox");
            string ToolboxSettingsPath = Path.Combine(ToolboxPath, ".settings.json");
            ToolboxSettingsFile SettingsFile = JsonConvert.DeserializeObject<ToolboxSettingsFile>(File.ReadAllText(ToolboxSettingsPath));

            string ToolboxInstallLocation = ToolboxPath;
            if (!string.IsNullOrEmpty(SettingsFile.InstallLocation))
            {
                ToolboxInstallLocation = SettingsFile.InstallLocation;
            }

            string ToolboxRiderPath = Path.Combine(ToolboxInstallLocation, "apps\\Rider\\ch-0");

            if(Directory.Exists(ToolboxRiderPath))
            {
                string[] SubDirs = Directory.GetDirectories(ToolboxRiderPath);
                Version LatestVersion = null;
                string LatestVersionPath = null;
                foreach(string SubDir in SubDirs)
                {
                    string DirName = Path.GetFileName(SubDir);
                    Version Version;
                    bool IsVersion = Version.TryParse(DirName, out Version);
                    if(!IsVersion)
                    { 
                        continue;
                    }
                    if(LatestVersion == null || Version > LatestVersion)
                    {
                        LatestVersion = Version;
                        LatestVersionPath = SubDir;
                    }
                }
                return LatestVersionPath;
            }

            RegistryKey riderVersionKey = GetRiderVersionKey("Rider for Unreal Engine");

            if (riderVersionKey is null)
            {
                riderVersionKey = GetRiderVersionKey("JetBrains Rider");
            }

            if (riderVersionKey is null)
            {
                RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                RegistryKey rider = currentUser.OpenSubKey(@"SOFTWARE\JetBrains\Rider");
                string[] subKeyNames = rider.GetSubKeyNames();
                string bestInstallDir = null;
                int bestProductVersion = 0;
                foreach (string subKeyName in subKeyNames)
                {
                    RegistryKey subKey = rider.OpenSubKey(subKeyName);
                    string installDir = subKey.GetValue("InstallDir") as string;
                    if(installDir == null)
                    {
                        continue;
                    }
                    int productVersion = int.Parse(subKey.GetValue("ProductVersion") as string);
                    if(installDir != null && productVersion > bestProductVersion)
                    {
                        bestInstallDir = installDir;
                        bestProductVersion = productVersion;
                    }
                }
                return bestInstallDir;
            }

            if (riderVersionKey is null)
            {
                throw new Exception("Couldn't find Rider path in registry");
            }

            var path = riderVersionKey.GetValue(null) as string;
            return path;
        }

        public static string FindExePath()
        {
            return Path.Combine(FindPath(), "bin", "rider64.exe");
        }

        private static RegistryKey GetRiderVersionKey(string riderKeyName)
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            RegistryKey jetBrains = localMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\JetBrains");

            string[] subKeyNames = jetBrains.GetSubKeyNames();

            if (!subKeyNames.Contains(riderKeyName))
            {
                return null;
            }

            RegistryKey localMachineRider = jetBrains.OpenSubKey(riderKeyName);

            string[] riderSubKeyNames = localMachineRider.GetSubKeyNames();

            if (riderSubKeyNames.Length == 0)
            {
                return null;
            }

            return localMachineRider.OpenSubKey(riderSubKeyNames[0]);
        }
    }
}