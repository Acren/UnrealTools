using Microsoft.Win32;
using System.IO;

namespace UnrealAutomationCommon
{
    public class ProjectUtils
    {
        // Does not include the actual "Engine" subdirectory
        public static string GetEngineInstallDirectory(string EngineAssociation)
        {
            RegistryKey BaseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            RegistryKey Key = BaseKey.OpenSubKey(@"Software\EpicGames\Unreal Engine\" + EngineAssociation);
            string EnginePath = (string)Key.GetValue("InstalledDirectory");
            return EnginePath;
        }
    }
}
