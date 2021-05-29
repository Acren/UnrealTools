using Microsoft.Win32;
using System;
using System.Linq;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon
{
    public class EngineInstall
    {
        public string InstallDirectory { get; set; }
        public bool IsSourceBuild = false;

        // Does not include the actual "Engine" subdirectory
        public static EngineInstall GetEngineInstallFromRegistry(string EngineAssociation)
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
                return new EngineInstall()
                {
                    InstallDirectory = (string) localMachineEngineVersion.GetValue("InstalledDirectory"),
                    IsSourceBuild = false
                };
            }
            RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            RegistryKey currentUserBuilds = currentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            string userBuildValue = (string)currentUserBuilds.GetValue(EngineAssociation);
            if (userBuildValue != null)
            {
                return new EngineInstall()
                {
                    InstallDirectory = userBuildValue.Replace('/', '\\'),
                    IsSourceBuild = true
                };
            }

            return null;
        }

        public static EngineInstall GetEngineInstallFromLauncherManifest(string EngineAssociation)
        {
            LauncherInstalledEngineManifest Manifest = LauncherInstalledEngineManifest.Load();
            foreach (EngineInstallation engine in Manifest.InstallationList)
            {
                if (engine.AppName.Contains(EngineAssociation))
                {
                    return new EngineInstall()
                    {
                        InstallDirectory = engine.InstallLocation,
                        IsSourceBuild = false
                    };
                }
            }

            return null;
        }

        public static EngineInstall GetEngineInstall(string EngineAssociation)
        {
            EngineInstall EngineInstall = GetEngineInstallFromLauncherManifest(EngineAssociation);
            if (EngineInstall != null)
            {
                return EngineInstall;
            }

            EngineInstall = GetEngineInstallFromRegistry(EngineAssociation);
            if (EngineInstall != null)
            {
                return EngineInstall;
            }

            throw new Exception("Could not find Engine installation based on EngineAssociation: + " + EngineAssociation);
        }

        public EngineInstallVersion GetVersion()
        {
            return EngineInstallVersion.Load(this.GetBuildVersionPath());
        }

        public bool SupportsConfiguration(BuildConfiguration Configuration)
        {
            if (Configuration == BuildConfiguration.Debug
                || Configuration == BuildConfiguration.Test)
            {
                // Only support Debug and Test in source builds
                return IsSourceBuild;
            }

            // Always support DebugGame, Development, Shipping
            return true;
        }
    }
}
