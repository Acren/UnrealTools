using Microsoft.Win32;
using System.IO;

namespace UnrealAutomationCommon
{
    public class ProjectUtils
    {
        // Does not include the actual "Engine" subdirectory
        public static string GetEngineInstallDirectory(string EngineAssociation)
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            RegistryKey localMachineEngineVersion = localMachine.OpenSubKey(@"Software\EpicGames\Unreal Engine\" + EngineAssociation);
            if (localMachineEngineVersion != null)
            {
                return (string)localMachineEngineVersion.GetValue("InstalledDirectory");
            }
            RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            RegistryKey currentUserBuilds = currentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            return (string)currentUserBuilds.GetValue(EngineAssociation);
        }

        public static bool IsProjectFile(string FilePath)
        {
            return Path.GetExtension(FilePath).Equals(".uproject", System.StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
