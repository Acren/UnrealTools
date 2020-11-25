using System;
using Microsoft.Win32;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon
{
    public class ProjectUtils
    {
        // Does not include the actual "Engine" subdirectory
        public static string GetEngineInstallDirectoryFromRegistry(string EngineAssociation)
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            if (EngineAssociation.Contains("."))
            {
                // It's a launcher version
                if (EngineAssociation.Count(c => c == '.') > 1)
                {
                    // Trim patch from version number
                    EngineAssociation = EngineAssociation.Remove(EngineAssociation.LastIndexOf('.'));
                }
            }

            RegistryKey localMachineEngineVersion = localMachine.OpenSubKey(@"Software\EpicGames\Unreal Engine\" + EngineAssociation);
            if (localMachineEngineVersion != null)
            {
                return (string)localMachineEngineVersion.GetValue("InstalledDirectory");
            }
            RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            RegistryKey currentUserBuilds = currentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            string userBuildValue = (string)currentUserBuilds.GetValue(EngineAssociation);
            if (userBuildValue != null)
            {
                return userBuildValue.Replace('/','\\');
            }

            return null;
        }

        public static string GetEngineInstallDirectoryFromLauncherManifest(string EngineAssociation)
        {
            LauncherInstalledEngineManifest Manifest = LauncherInstalledEngineManifest.Load();
            foreach(EngineInstallation engine in Manifest.InstallationList)
            {
                if(engine.AppName.Contains(EngineAssociation))
                {
                    return engine.InstallLocation;
                }
            }

            return null;
        }

        public static string GetEngineInstallDirectory(string EngineAssociation)
        {
            string Directory = GetEngineInstallDirectoryFromLauncherManifest(EngineAssociation);
            if (Directory != null)
            {
                return Directory;
            }

            Directory = GetEngineInstallDirectoryFromRegistry(EngineAssociation);
            if (Directory != null)
            {
                return Directory;
            }

            throw new Exception("Could not find Engine installation based on EngineAssociation: + " + EngineAssociation);
        }

        public static bool IsProjectFile(string FilePath)
        {
            return Path.GetExtension(FilePath).Equals(".uproject", System.StringComparison.InvariantCultureIgnoreCase);
        }
    }
}
