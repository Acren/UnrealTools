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
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            RegistryKey jetBrains = localMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\JetBrains");

            string[] subKeyNames = jetBrains.GetSubKeyNames();

            RegistryKey riderVersionKey = GetRiderVersionKey(jetBrains, "Rider for Unreal Engine");

            if (riderVersionKey is null)
            {
                riderVersionKey = GetRiderVersionKey(jetBrains, "JetBrains Rider");
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

        private static RegistryKey GetRiderVersionKey(RegistryKey jetBrains, string riderKeyName)
        {
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