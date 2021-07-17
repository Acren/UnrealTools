using Microsoft.Win32;
using System.Collections.Generic;
using System.Linq;

namespace UnrealAutomationCommon.Unreal
{
    public static class EngineInstallFinder
    {
        public static List<EngineInstall> GetEngineInstallsFromRegistry()
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            RegistryKey localMachineUnrealEngine = localMachine.OpenSubKey(@"Software\EpicGames\Unreal Engine");
            if (localMachineUnrealEngine == null)
            {
                return new List<EngineInstall>();
            }

            string[] subKeys = localMachineUnrealEngine.GetSubKeyNames();

            List<EngineInstall> result = new List<EngineInstall>();

            foreach (string subKeyString in subKeys)
            {
                RegistryKey engineVersionKey = localMachineUnrealEngine.OpenSubKey(subKeyString);
                result.Add(new EngineInstall()
                {
                    Key = subKeyString,
                    InstallDirectory = (string)engineVersionKey.GetValue("InstalledDirectory"),
                    IsSourceBuild = false
                });
            }

            RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            RegistryKey currentUserBuilds = currentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");

            string[] buildValueNames = currentUserBuilds.GetValueNames();
            foreach (string buildName in buildValueNames)
            {
                string buildPath = (string)currentUserBuilds.GetValue(buildName);
                if (buildPath != null)
                {
                    result.Add(new EngineInstall()
                    {
                        Key = buildName,
                        InstallDirectory = buildPath.Replace('/', '\\'),
                        IsSourceBuild = true
                    });
                }
            }

            return result;
        }

        public static EngineInstall GetEngineInstallFromRegistry(string engineAssociation)
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            if (engineAssociation.Contains("."))
            {
                // It's a launcher version
                if (engineAssociation.Count(c => c == '.') > 1)
                {
                    // Trim patch from version number
                    engineAssociation = engineAssociation.Remove(engineAssociation.LastIndexOf('.'));
                }
            }

            return GetEngineInstallsFromRegistry().Find(x => x.Key == engineAssociation);
        }

        public static List<EngineInstall> GetEngineInstallsFromLauncherManifest()
        {
            List<EngineInstall> result = new List<EngineInstall>();
            LauncherInstalledEngineManifest manifest = LauncherInstalledEngineManifest.Load();
            foreach (LauncherManifestAppInstallation app in manifest.InstallationList)
            {
                if (app.AppName.StartsWith("UE_"))
                {
                    // It's an engine install
                    string trimmedName = app.AppName.Replace("UE_", "");
                    result.Add(new EngineInstall()
                    {
                        Key = trimmedName,
                        InstallDirectory = app.InstallLocation,
                        IsSourceBuild = false
                    });
                }
            }

            return result;
        }

        public static EngineInstall GetEngineInstallFromLauncherManifest(string engineAssociation)
        {
            return GetEngineInstallsFromLauncherManifest().Find(x => x.Key == engineAssociation);
        }

        public static List<EngineInstall> GetEngineInstalls()
        {
            List<EngineInstall> installs = GetEngineInstallsFromRegistry();
            installs.AddRange(GetEngineInstallsFromLauncherManifest());
            return installs;
        }

        public static EngineInstall GetDefaultEngineInstall()
        {
            return GetEngineInstalls().Last();
        }

        public static EngineInstall GetEngineInstall(string engineKey)
        {
            if (engineKey == null)
            {
                return GetDefaultEngineInstall();
            }

            EngineInstall engineInstall = GetEngineInstalls().Find(x => x.Key == engineKey);
            if (engineInstall != null)
            {
                return engineInstall;
            }

            return null;
            //throw new Exception("Could not find Engine installation based on EngineAssociation: + " + engineKey);
        }

        public static EngineInstall GetEngineInstall(EngineInstallVersion version)
        {
            return GetEngineInstalls().Find(x => x.GetVersion().MinorVersionEquals(version));
        }
    }
}
