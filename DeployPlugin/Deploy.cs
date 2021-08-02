﻿using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Unreal;

namespace DeployPlugin
{
    class Deploy
    {
        public static void RunDeployment(DeployParams Params)
        {
            string PluginPath = Params.PluginPath;
            ConsoleUtils.WriteHeader("Begin Deployment");
            Console.WriteLine("Deploying plugin at " + PluginPath);

            // Get uplugin path
            string UPluginPath = DeployUtils.FindUPluginPath(PluginPath);

            Console.WriteLine("Identified " + Path.GetFileName(UPluginPath) + " as plugin definition");

            string PluginName = DeployUtils.FindPluginName(PluginPath);

            // Deserialize uplugin
            PluginDescriptor PluginDef = JsonConvert.DeserializeObject<PluginDescriptor>(File.ReadAllText(UPluginPath));

            string ProjectPath = DeployUtils.FindHostProjectPath(PluginPath);
            string UProjectPath = DeployUtils.FindHostUProjectPath(PluginPath);

            Console.WriteLine("Identified " + Path.GetFileName(UProjectPath) + " as host project definition");

            ProjectDescriptor ProjectDef = ProjectDescriptor.Load(UProjectPath);

            if (!ProjectDef.HasPluginEnabled(PluginName))
            {
                throw new Exception(".uproject does not list " + PluginName + " as an enabled plugin dependency");
            }

            // Trim patch version
            if (PluginDef.EngineVersion != null)
            {
                string[] PluginEngineVersionSplit = PluginDef.EngineVersion.Split('.');
                string PluginEngineVersion = PluginEngineVersionSplit[0] + '.' + PluginEngineVersionSplit[1];

                // Check engine versions match
                if (PluginEngineVersion != ProjectDef.EngineAssociation)
                {
                    throw new Exception("Project engine version " + ProjectDef.EngineAssociation + " does not match plugin engine version " + PluginDef.EngineVersion);
                }
            }
            else
            {
                bool Continue = ConsoleUtils.PromptBool(".uplugin has no EngineVersion. Project engine version is " + ProjectDef.EngineAssociation + ". Continue?", true);
                if (!Continue)
                {
                    return;
                }
            }

            string branchName = VersionControlUtils.GetBranchName(ProjectPath);
            string archiveVersionName = null;

            if (Params.Archive)
            {
                // Use the version if on any of these branches
                string[] standardBranchNames = { "master", "develop", "development" };

                if (!branchName.StartsWith("version/", StringComparison.InvariantCultureIgnoreCase) && !standardBranchNames.Contains(branchName, StringComparer.InvariantCultureIgnoreCase))
                {
                    Console.WriteLine("On branch '" + branchName + "' which isn't a version or standard branch");
                    archiveVersionName = branchName.Replace("/", "-");
                }
                else
                {
                    archiveVersionName = PluginDef.VersionName;
                }
                Console.WriteLine("Archive version name is '" + archiveVersionName + "'");
            }

            // Begin work

            ConsoleUtils.WriteHeader("Preparing plugin");

            // Get engine path

            string EnginePath = ProjectDef.GetEngineInstall().InstallDirectory;

            string EnginePluginsMarketplacePath = Path.Combine(EnginePath, @"Engine\Plugins\Marketplace");
            string EnginePluginsMarketplacePluginPath = Path.Combine(EnginePluginsMarketplacePath, Path.GetFileName(PluginPath));

            // Delete plugin from engine if installed version exists
            ConsoleFileUtils.DeleteDirectoryIfExists(EnginePluginsMarketplacePluginPath);

            //string WorkingTempPath = Path.Combine(Path.GetTempPath(), "MarketplaceDeploy", PluginName);
            //string WorkingTempPath = Path.Combine(Path.GetPathRoot(Path.GetTempPath()), "MarketplaceDeploy", PluginName);
            string WorkingTempPath = Path.Combine(Path.GetPathRoot(Path.GetTempPath()), PluginName);

            Directory.CreateDirectory(WorkingTempPath);

            // Build plugin

            ConsoleUtils.WriteHeader("Building plugin");

            string PluginBuildPath = Path.Combine(WorkingTempPath, @"PluginBuild", PluginName);

            ConsoleFileUtils.DeleteDirectoryIfExists(PluginBuildPath);

            ProcessStartInfo PluginBuildStartInfo = new ProcessStartInfo()
            {
                Arguments = "BuildPlugin -Plugin=\"" + UPluginPath + "\" -Package=\"" + PluginBuildPath + "\" -Rocket -VS2019",
                FileName = Path.Combine(EnginePath, @"Engine\Build\BatchFiles\RunUAT.bat"),
                UseShellExecute = false
            };

            Process PluginBuildProcess = new Process();
            PluginBuildProcess.StartInfo = PluginBuildStartInfo;
            PluginBuildProcess.Start();
            PluginBuildProcess.WaitForExit();

            if (PluginBuildProcess.ExitCode > 0)
            {
                throw new Exception("Plugin build failed");
            }

            Console.WriteLine("Plugin build complete");

            // Copy config directory to plugin build because the build process doesn't do so

            string SourcePluginConfigPath = Path.Combine(PluginPath, @"Config");
            string PluginBuildConfigPath = Path.Combine(PluginBuildPath, @"Config");

            ConsoleFileUtils.CopyDirectory(SourcePluginConfigPath, PluginBuildConfigPath);

            // Copy plugin into engine where the marketplace installs it

            Console.WriteLine("Copying to Engine/Plugins/Marketplace");

            ConsoleFileUtils.DeleteDirectoryIfExists(EnginePluginsMarketplacePluginPath);

            ConsoleFileUtils.CopyDirectory(PluginBuildPath, EnginePluginsMarketplacePluginPath);

            // Set up host project

            ConsoleUtils.WriteHeader("Preparing host project");

            string UProjectFilename = Path.GetFileName(UProjectPath);
            string ProjectName = Path.GetFileNameWithoutExtension(UProjectPath);

            string ExampleProjectBuildPath = Path.Combine(WorkingTempPath, @"ExampleProject");

            ConsoleFileUtils.DeleteDirectoryIfExists(ExampleProjectBuildPath);

            ConsoleFileUtils.CopySubdirectory(ProjectPath, ExampleProjectBuildPath, "Content");
            ConsoleFileUtils.CopySubdirectory(ProjectPath, ExampleProjectBuildPath, "Config");
            if (!Params.RemoveSource)
            {
                ConsoleFileUtils.CopySubdirectory(ProjectPath, ExampleProjectBuildPath, "Source");
            }

            string ProjectIcon = ProjectName + ".png";
            if (File.Exists(ProjectIcon))
            {
                ConsoleFileUtils.CopyFile(ProjectPath, ExampleProjectBuildPath, ProjectIcon);
            }

            // Copy uproject 
            JObject UProjectContents = JObject.Parse(File.ReadAllText(UProjectPath));

            // Remove modules property if applicable before building
            if (Params.RemoveSource)
            {
                UProjectContents.Remove("Modules");
            }

            string ExampleProjectBuildUProjectPath = Path.Combine(ExampleProjectBuildPath, UProjectFilename);

            File.WriteAllText(ExampleProjectBuildUProjectPath, UProjectContents.ToString());

            // Build example project without archiving to test that it can package with plugin installed to engine
            // It's worth doing this to test for build or packaging issues that might only happen using installed plugin

            Console.WriteLine("Building example project with installed plugin");

            string InstalledPluginTestBuildArchivePath = Path.Combine(WorkingTempPath, @"InstalledPluginTestBuild");
            ConsoleFileUtils.DeleteDirectoryIfExists(InstalledPluginTestBuildArchivePath);

            ProcessStartInfo InstalledPluginTestBuildStartInfo = new ProcessStartInfo()
            {
                Arguments = "BuildCookRun -project=\"" + ExampleProjectBuildUProjectPath + "\" -noP4 -platform=Win64 -clientconfig=Development -serverconfig=Development -cook -allmaps -build -stage " + DeployUtils.GetPakString(Params.Pak) + "-archive -archivedirectory=\"" + InstalledPluginTestBuildArchivePath + "\"",
                FileName = Path.Combine(EnginePath, @"Engine\Build\BatchFiles\RunUAT.bat"),
                UseShellExecute = false
            };

            Process InstalledPluginTestBuildProcess = new Process();
            InstalledPluginTestBuildProcess.StartInfo = InstalledPluginTestBuildStartInfo;
            InstalledPluginTestBuildProcess.Start();
            InstalledPluginTestBuildProcess.WaitForExit();

            if (InstalledPluginTestBuildProcess.ExitCode > 0)
            {
                throw new Exception("Example project build with installed plugin failed");
            }

            // Uninstall plugin from engine because test has completed
            // Now we'll be using the plugin in the project directory instead

            Console.WriteLine("Uninstall from Engine/Plugins/Marketplace");

            ConsoleFileUtils.DeleteDirectoryIfExists(EnginePluginsMarketplacePluginPath);

            // Copy plugin to example project to prepare for package
            string ExampleProjectPluginPath = Path.Combine(ExampleProjectBuildPath, "Plugins", Path.GetFileName(PluginPath));
            ConsoleFileUtils.CopyDirectory(PluginBuildPath, ExampleProjectPluginPath);

            // Package game for demo

            ConsoleUtils.WriteHeader("Packaging host project for demo");

            string DemoExePath = Path.Combine(WorkingTempPath, @"DemoExe");

            ConsoleFileUtils.DeleteDirectoryIfExists(DemoExePath);

            ProcessStartInfo DemoExeStartInfo = new ProcessStartInfo()
            {
                Arguments = "BuildCookRun -project=\"" + ExampleProjectBuildUProjectPath + "\" -noP4 -platform=Win64 -clientconfig=Development -serverconfig=Development -cook -allmaps -build -stage " + DeployUtils.GetPakString(Params.Pak) + "-archive -archivedirectory=\"" + DemoExePath + "\"",
                FileName = Path.Combine(EnginePath, @"Engine\Build\BatchFiles\RunUAT.bat"),
                UseShellExecute = false
            };

            Process DemoExeBuildProcess = new Process();
            DemoExeBuildProcess.StartInfo = DemoExeStartInfo;
            DemoExeBuildProcess.Start();
            DemoExeBuildProcess.WaitForExit();

            if (DemoExeBuildProcess.ExitCode > 0)
            {
                throw new Exception("Example project build failed");
            }

            // Archiving

            if (Params.Archive)
            {
                ConsoleUtils.WriteHeader("Archiving");

                string ArchivePrefix = PluginName + "_" + archiveVersionName + "_";

                string ArchivePath = Path.Combine(WorkingTempPath, "Archives");

                Directory.CreateDirectory(ArchivePath);

                // Archive plugin build

                Console.WriteLine("Archiving plugin build");
                string PluginBuildZipPath = Path.Combine(ArchivePath, ArchivePrefix + "PluginBuild.zip");
                ConsoleFileUtils.DeleteFile(PluginBuildZipPath);
                ZipFile.CreateFromDirectory(PluginBuildPath, PluginBuildZipPath, CompressionLevel.Optimal, true);

                // Archive demo exe

                Console.WriteLine("Archiving demo");

                string DemoExeZipPath = Path.Combine(ArchivePath, ArchivePrefix + "DemoExe.zip");
                ConsoleFileUtils.DeleteFile(DemoExeZipPath);
                ZipFile.CreateFromDirectory(Path.Combine(DemoExePath, "WindowsNoEditor"), DemoExeZipPath);

                // Archive example project

                Console.WriteLine("Archiving example project");

                // First delete any extra directories
                string[] AllowedExampleProjectSubDirectoryNames = { "Content", "Config" };
                ConsoleFileUtils.DeleteOtherSubdirectories(ExampleProjectBuildPath, AllowedExampleProjectSubDirectoryNames);

                // If we didn't remove source before, remove it now since we never want it in the example project
                if (!Params.RemoveSource)
                {
                    UProjectContents.Remove("Modules");
                    File.WriteAllText(ExampleProjectBuildUProjectPath, UProjectContents.ToString());
                }

                string ExampleProjectZipPath = Path.Combine(ArchivePath, ArchivePrefix + "ExampleProject.zip");
                ConsoleFileUtils.DeleteFile(ExampleProjectZipPath);
                ZipFile.CreateFromDirectory(ExampleProjectBuildPath, ExampleProjectZipPath);

                // Archive plugin for submission

                Console.WriteLine("Archiving plugin");

                // Copy plugin build to submission so that we can prepare archive for submission without ruining build
                string PluginSubmissionPath = Path.Combine(WorkingTempPath, @"PluginSubmission", PluginName);
                ConsoleFileUtils.DeleteDirectoryIfExists(PluginSubmissionPath);
                ConsoleFileUtils.CopyDirectory(PluginBuildPath, PluginSubmissionPath);

                string[] AllowedPluginSubmissionSubDirectoryNames = { "Source", "Resources", "Content", "Config" };
                ConsoleFileUtils.DeleteOtherSubdirectories(PluginSubmissionPath, AllowedPluginSubmissionSubDirectoryNames);

                string PluginSubmissionZipPath = Path.Combine(ArchivePath, ArchivePrefix + "PluginSubmission.zip");
                ConsoleFileUtils.DeleteFile(PluginSubmissionZipPath);
                ZipFile.CreateFromDirectory(PluginSubmissionPath, PluginSubmissionZipPath, CompressionLevel.Optimal, true);

                // Upload to staging folder on drive

                if (Params.Upload)
                {
                    ConsoleUtils.WriteHeader("Uploading");

                    Console.WriteLine("Uploading plugin");
                    DriveIntegration.UploadFile(PluginSubmissionZipPath);

                    Console.WriteLine("Uploading example project");
                    DriveIntegration.UploadFile(ExampleProjectZipPath);

                    Console.WriteLine("Uploading demo");
                    DriveIntegration.UploadFile(DemoExeZipPath);
                }
                else
                {
                    Console.WriteLine("Skipping upload");
                }
            }
            else
            {
                Console.WriteLine("Skipping archive");
            }

            ConsoleUtils.WriteHeader("MarketplaceDeployConsole finished successfully");

        }
    }
}
