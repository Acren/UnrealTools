using System;
using Microsoft.Win32;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon
{
    public static class Rider
    {
        public static string FindPath()
        {
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