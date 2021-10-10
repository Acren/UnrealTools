using System.IO;
using Microsoft.Win32;

namespace UnrealAutomationCommon
{
    public static class Rider
    {
        public static string FindPath()
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            RegistryKey localMachineRider = localMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\JetBrains\Rider for Unreal Engine");

            string subkeyName = localMachineRider.GetSubKeyNames()[0];
            RegistryKey version = localMachineRider.OpenSubKey(subkeyName);
            var path = version.GetValue(null) as string;
            return path;
        }

        public static string FindExePath()
        {
            return Path.Combine(FindPath(), "bin", "rider64.exe");
        }
    }
}