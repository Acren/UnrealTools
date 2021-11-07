using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon.Unreal
{
    public static class EngineInstallFinder
    {
        public static bool IsEngineInstallDirectory(string path)
        {
            return Directory.Exists(path);
        }

        public static List<EngineInstall> GetEngineInstallsFromRegistry()
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            RegistryKey localMachineUnrealEngine = localMachine.OpenSubKey(@"Software\EpicGames\Unreal Engine");
            if (localMachineUnrealEngine == null) return new List<EngineInstall>();

            string[] subKeys = localMachineUnrealEngine.GetSubKeyNames();

            var result = new List<EngineInstall>();

            foreach (string subKeyString in subKeys)
            {
                RegistryKey engineVersionKey = localMachineUnrealEngine.OpenSubKey(subKeyString);
                var directory = (string)engineVersionKey.GetValue("InstalledDirectory");
                if (IsEngineInstallDirectory(directory))
                    result.Add(new EngineInstall
                    {
                        Key = subKeyString,
                        InstallDirectory = directory,
                        IsSourceBuild = false
                    });
            }

            RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            RegistryKey currentUserBuilds = currentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");

            string[] buildValueNames = currentUserBuilds.GetValueNames();
            foreach (string buildName in buildValueNames)
            {
                var buildPath = (string)currentUserBuilds.GetValue(buildName);
                if (buildPath == null) continue;
                buildPath = buildPath.Replace('/', '\\');
                if (IsEngineInstallDirectory(buildPath))
                    result.Add(new EngineInstall
                    {
                        Key = buildName,
                        InstallDirectory = buildPath,
                        IsSourceBuild = true
                    });
            }

            return result;
        }

        public static EngineInstall GetEngineInstallFromRegistry(string engineAssociation)
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            if (engineAssociation.Contains("."))
                // It's a launcher version
                if (engineAssociation.Count(c => c == '.') > 1)
                    // Trim patch from version number
                    engineAssociation = engineAssociation.Remove(engineAssociation.LastIndexOf('.'));

            return GetEngineInstallsFromRegistry().Find(x => x.Key == engineAssociation);
        }

        public static List<EngineInstall> GetEngineInstallsFromLauncherManifest()
        {
            var result = new List<EngineInstall>();
            LauncherInstalledEngineManifest manifest = LauncherInstalledEngineManifest.Load();
            foreach (LauncherManifestAppInstallation app in manifest.InstallationList)
                if (app.AppName.StartsWith("UE_"))
                {
                    // It's an engine install
                    string trimmedName = app.AppName.Replace("UE_", "");
                    result.Add(new EngineInstall
                    {
                        Key = trimmedName,
                        InstallDirectory = app.InstallLocation,
                        IsSourceBuild = false
                    });
                }

            return result;
        }

        public static EngineInstall GetEngineInstallFromLauncherManifest(string engineAssociation)
        {
            return GetEngineInstallsFromLauncherManifest().Find(x => x.Key == engineAssociation);
        }

        public static List<EngineInstall> GetEngineInstalls()
        {
            var installs = GetEngineInstallsFromRegistry();
            installs.AddRange(GetEngineInstallsFromLauncherManifest());
            return installs;
        }

        public static EngineInstall GetDefaultEngineInstall()
        {
            return GetEngineInstalls().Last();
        }

        public static EngineInstall GetEngineInstall(string engineKey)
        {
            if (engineKey == null) return GetDefaultEngineInstall();

            // Check Contains so that engine with "5.0EA" satisfies search for "5.0"
            EngineInstall engineInstall = GetEngineInstalls().Find(x => x.Key.Contains(engineKey));
            if (engineInstall != null) return engineInstall;

            throw new Exception("Could not find Engine installation based on EngineKey: + " + engineKey);
        }

        public static EngineInstall GetEngineInstall(EngineInstallVersion version)
        {
            if (version == null) throw new Exception("Invalid version");

            return GetEngineInstalls().Find(x => x.Version.MinorVersionEquals(version));
        }
    }
}