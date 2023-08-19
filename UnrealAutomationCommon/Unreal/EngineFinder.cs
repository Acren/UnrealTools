using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnrealAutomationCommon.Unreal
{
    public static class EngineFinder
    {
        public static bool IsEngineInstallDirectory(string path)
        {
            return Directory.Exists(path);
        }

        public static List<Engine> GetEngineInstallsFromRegistry()
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            RegistryKey localMachineUnrealEngine = localMachine.OpenSubKey(@"Software\EpicGames\Unreal Engine");
            if (localMachineUnrealEngine == null)
            {
                return new List<Engine>();
            }

            string[] subKeys = localMachineUnrealEngine.GetSubKeyNames();

            var result = new List<Engine>();

            foreach (string subKeyString in subKeys)
            {
                RegistryKey engineVersionKey = localMachineUnrealEngine.OpenSubKey(subKeyString);
                var directory = (string)engineVersionKey.GetValue("InstalledDirectory");
                if (IsEngineInstallDirectory(directory))
                {
                    result.Add(new Engine(directory)
                    {
                        Key = subKeyString
                    });
                }
            }

            RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            RegistryKey currentUserBuilds = currentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");

            if(currentUserBuilds == null)
            {
                return result;
            }

            string[] buildValueNames = currentUserBuilds.GetValueNames();
            foreach (string buildName in buildValueNames)
            {
                var buildPath = (string)currentUserBuilds.GetValue(buildName);
                if (buildPath == null)
                {
                    continue;
                }

                buildPath = buildPath.Replace('/', '\\');
                if (IsEngineInstallDirectory(buildPath))
                {
                    result.Add(new Engine(buildPath)
                    {
                        Key = buildName
                    });
                }
            }

            return result;
        }

        public static Engine GetEngineInstallFromRegistry(string engineAssociation)
        {
            RegistryKey localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            if (engineAssociation.Contains("."))
                // It's a launcher version
            {
                if (engineAssociation.Count(c => c == '.') > 1)
                    // Trim patch from version number
                {
                    engineAssociation = engineAssociation.Remove(engineAssociation.LastIndexOf('.'));
                }
            }

            return GetEngineInstallsFromRegistry().Find(x => x.Key == engineAssociation);
        }

        public static List<Engine> GetEngineInstallsFromLauncherManifest()
        {
            var result = new List<Engine>();
            LauncherInstalledEngineManifest manifest = LauncherInstalledEngineManifest.Load();
            foreach (LauncherManifestAppInstallation app in manifest.InstallationList)
                if (app.AppName.StartsWith("UE_"))
                {
                    // It's an engine install
                    string trimmedName = app.AppName.Replace("UE_", "");
                    result.Add(new Engine(app.InstallLocation)
                    {
                        Key = trimmedName
                    });
                }

            return result;
        }

        public static Engine GetEngineInstallFromLauncherManifest(string engineAssociation)
        {
            return GetEngineInstallsFromLauncherManifest().Find(x => x.Key == engineAssociation);
        }

        public static List<Engine> GetEngineInstalls()
        {
            var installs = GetEngineInstallsFromRegistry();
            installs.AddRange(GetEngineInstallsFromLauncherManifest());
            return installs;
        }

        public static List<EngineVersion> GetLauncherEngineInstallVersions()
        {
            List<EngineVersion> versions = new();
            foreach(Engine engine in GetEngineInstalls())
            {
                if (!engine.IsSourceBuild && !versions.Contains(engine.Version))
                {
                    versions.Add(engine.Version);
                }
            }
            return versions;
        }

        public static Engine GetDefaultEngineInstall()
        {
            return GetEngineInstalls().Last();
        }

        public static Engine GetEngineInstall(string engineKey, bool defaultIfNotFound = false)
        {
            if (engineKey == null)
            {
                return GetDefaultEngineInstall();
            }

            // Check Contains so that engine with "5.0EA" satisfies search for "5.0"
            Engine engine = GetEngineInstalls().Find(x => x.Key.Contains(engineKey));
            if (engine != null)
            {
                return engine;
            }

            if (defaultIfNotFound)
            {
                return GetDefaultEngineInstall();
            }

            throw new Exception("Could not find Engine installation based on EngineKey: + " + engineKey);
        }

        public static Engine GetEngineInstall(EngineVersion version)
        {
            if (version == null)
            {
                throw new Exception("Invalid version");
            }

            return GetEngineInstalls().Find(x => x.Version.MinorVersionEquals(version));
        }
    }
}