using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class DeployPlugin : Operation<Plugin>
    {
        protected override async Task<OperationResult> OnExecuted(CancellationToken token)
        {
            OperationResult result = await DeployForEngine(null, token);

            return result;
        }

        private async Task<OperationResult> DeployForEngine(EngineInstall engine, CancellationToken token)
        {
            Logger.Log("Preparing plugin");

            Plugin plugin = GetTarget(OperationParameters);
            PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
            Project hostProject = plugin.HostProject;
            ProjectDescriptor projectDescriptor = hostProject.ProjectDescriptor;

            if (!projectDescriptor.HasPluginEnabled(plugin.Name))
            {
                throw new Exception("Host project must have plugin enabled");
            }

            EngineInstallVersion engineVersion = plugin.EngineInstall.Version;
            Logger.Log($"Engine version: {engineVersion}");

            string branchName = VersionControlUtils.GetBranchName(hostProject.GetProjectPath());
            Logger.Log($"Branch: {branchName}");
            // Use the version if on any of these branches
            string[] standardBranchNames = { "master", "develop", "development" };
            string[] standardBranchPrefixes = { "version/", "release/" };

            bool bStandardBranch = standardBranchNames.Contains(branchName, StringComparer.InvariantCultureIgnoreCase);
            if (!bStandardBranch)
            {
                foreach (string prefix in standardBranchPrefixes)
                {
                    if (branchName.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase))
                    {
                        bStandardBranch = true;
                        break;
                    }
                }
            }

            string archivePrefix = plugin.Name;

            bool beta = pluginDescriptor.IsBetaVersion;
            if (beta)
            {
                Logger.Log("Plugin is marked as beta version");
                archivePrefix += "_beta";
            }

            string pluginVersionString = pluginDescriptor.VersionName;
            string fullPluginVersionString;
            if (pluginDescriptor.VersionName.Contains(branchName))
            {
                Logger.Log("Branch is contained in plugin version name");
                fullPluginVersionString = pluginVersionString;
            }
            else if (engineVersion.ToString().Contains(branchName))
            {
                Logger.Log("Branch is contained in engine version");
                fullPluginVersionString = pluginVersionString;
            }
            else if (!bStandardBranch)
            {
                Logger.Log("Branch isn't a version or standard branch");
                fullPluginVersionString = $"{pluginVersionString}-{branchName.Replace("/", "-")}";
            }
            else
            {
                Logger.Log("Branch is a version or standard branch");
                fullPluginVersionString = pluginVersionString;
            }

            archivePrefix += $"_{fullPluginVersionString}";
            archivePrefix += $"_{engineVersion.MajorMinorString}";
            archivePrefix += "_";

            Logger.Log($"Archive name prefix is '{archivePrefix}'");

            // Get engine path

            string enginePath = projectDescriptor.EngineInstall.InstallDirectory;

            string enginePluginsMarketplacePath = Path.Combine(enginePath, @"Engine\Plugins\Marketplace");
            string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, plugin.Name);

            // Delete plugin from engine if installed version exists
            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);

            string workingTempPath = GetOperationTempPath();

            Directory.CreateDirectory(workingTempPath);

            // Check copyright notice

            string hostProjectDefaultGameConfig = Path.Combine(hostProject.TargetDirectory, "Config", "DefaultGame.ini");
            UnrealConfig config = new(hostProjectDefaultGameConfig);
            string copyrightNotice = config.GetSection("/Script/EngineSettings.GeneralProjectSettings").GetValue("CopyrightNotice");

            string sourcePath = Path.Combine(plugin.TargetDirectory, "Source");
            var expectedComment = $"// {copyrightNotice}";
            foreach (string file in Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                string firstLine;
                using (StreamReader reader = new(file))
                {
                    firstLine = reader.ReadLine();
                }

                if (firstLine != expectedComment)
                {
                    var lines = File.ReadAllLines(file).ToList();
                    if (firstLine.StartsWith("//"))
                    // Replace existing comment with expected comment
                    {
                        lines[0] = expectedComment;
                    }
                    else
                    // Insert expected comment
                    {
                        lines.Insert(0, expectedComment);
                    }

                    File.WriteAllLines(file, lines);
                    string relativePath = Path.GetRelativePath(sourcePath, file);
                    Logger.Log($"Updated copyright notice: {relativePath}");
                }
            }

            // Build host project editor

            Logger.Log("Building host project editor");

            OperationParameters buildEditorParams = new()
            {
                Target = hostProject
            };

            if (!(await new BuildEditor().Execute(buildEditorParams, Logger, token)).Success)
            {
                throw new Exception("Failed to build host project");
            }

            // Launch and test host project editor

            AutomationOptions automationOpts = OperationParameters.FindOptions<AutomationOptions>();
            automationOpts.TestNameOverride = plugin.TestName;

            if (automationOpts.RunTests)
            {
                Logger.Log("Launching and testing host project editor");

                OperationParameters launchEditorParams = new()
                {
                    Target = hostProject
                };
                launchEditorParams.SetOptions(automationOpts);

                if (!(await new LaunchEditor().Execute(launchEditorParams, Logger, token)).Success)
                {
                    throw new Exception("Failed to launch host project");
                }
            }

            // Launch and test standalone

            if (automationOpts.RunTests && OperationParameters.RequestOptions<PluginDeployOptions>().TestStandalone)
            {
                Logger.Log("Launching and testing standalone");

                OperationParameters launchStandaloneParams = new()
                {
                    Target = hostProject
                };
                launchStandaloneParams.SetOptions(automationOpts);

                if (!(await new LaunchStandalone().Execute(launchStandaloneParams, Logger, token)).Success)
                {
                    throw new Exception("Failed to launch standalone");
                }
            }

            // Build plugin

            Logger.Log("Building plugin");

            string pluginBuildPath = Path.Combine(workingTempPath, @"PluginBuild", plugin.Name);
            FileUtils.DeleteDirectoryIfExists(pluginBuildPath);

            OperationParameters buildPluginParams = new()
            {
                Target = plugin,
                OutputPathOverride = pluginBuildPath
            };
            buildPluginParams.SetOptions(OperationParameters.RequestOptions<PluginBuildOptions>());

            OperationResult buildResult = await new BuildPlugin().Execute(buildPluginParams, Logger, token);

            if (!buildResult.Success)
            {
                Logger.Log("Plugin build failed", LogVerbosity.Error);
                return new OperationResult(false);
            }

            Logger.Log("Plugin build complete");

            // Copy plugin into engine where the marketplace installs it

            Logger.Log("Copying to Engine/Plugins/Marketplace");

            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);

            FileUtils.CopyDirectory(pluginBuildPath, enginePluginsMarketplacePluginPath);

            // Set up host project

            Logger.Log("Preparing host project");

            string uProjectFilename = Path.GetFileName(hostProject.UProjectPath);
            string projectName = Path.GetFileNameWithoutExtension(hostProject.UProjectPath);

            string exampleProjectBuildPath = Path.Combine(workingTempPath, @"ExampleProject");

            FileUtils.DeleteDirectoryIfExists(exampleProjectBuildPath);

            FileUtils.CopySubdirectory(hostProject.GetProjectPath(), exampleProjectBuildPath, "Content");
            FileUtils.CopySubdirectory(hostProject.GetProjectPath(), exampleProjectBuildPath, "Config");

            string projectIcon = projectName + ".png";
            if (File.Exists(projectIcon))
            {
                FileUtils.CopyFile(hostProject.GetProjectPath(), exampleProjectBuildPath, projectIcon);
            }

            // Copy other plugins

            var sourcePlugins = hostProject.GetPlugins();
            foreach (Plugin sourcePlugin in sourcePlugins)
                if (!sourcePlugin.Equals(plugin))
                {
                    FileUtils.CopyDirectory(sourcePlugin.TargetDirectory, Path.Combine(exampleProjectBuildPath, "Plugins"), true);
                }

            // Copy uproject 
            JObject uProjectContents = JObject.Parse(File.ReadAllText(hostProject.UProjectPath));

            // Remove modules property, which makes it a Blueprint-only project
            uProjectContents.Remove("Modules");

            string exampleProjectBuildUProjectPath = Path.Combine(exampleProjectBuildPath, uProjectFilename);

            File.WriteAllText(exampleProjectBuildUProjectPath, uProjectContents.ToString());

            // Build example project without archiving to test that it can package with plugin installed to engine
            // It's worth doing this to test for build or packaging issues that might only happen using installed plugin

            Logger.Log("Building example project with installed plugin");

            string installedPluginTestBuildArchivePath = Path.Combine(workingTempPath, @"InstalledPluginTestBuild");
            FileUtils.DeleteDirectoryIfExists(installedPluginTestBuildArchivePath);

            Project exampleProjectBuild = new(exampleProjectBuildUProjectPath);

            PackageProject installedPluginPackageOperation = new();
            OperationParameters installedPluginPackageParams = new()
            {
                Target = exampleProjectBuild,
                OutputPathOverride = exampleProjectBuildPath
            };

            OperationResult installedPluginPackageOperationResult = await installedPluginPackageOperation.Execute(installedPluginPackageParams, Logger, token);

            if (!installedPluginPackageOperationResult.Success)
            {
                throw new Exception("Example project build with installed plugin failed");
            }

            // Test the package

            if (automationOpts.RunTests && OperationParameters.RequestOptions<PluginDeployOptions>().TestPackage)
            {
                OperationParameters testInstalledPluginBuildParams = new()
                {
                    Target = exampleProjectBuild
                };
                testInstalledPluginBuildParams.SetOptions(automationOpts);

                OperationResult testResult = await new LaunchStagedPackage().Execute(testInstalledPluginBuildParams, Logger, token);

                if (!testResult.Success)
                {
                    throw new Exception("Launch and test with installed plugin failed");
                }
            }

            // Uninstall plugin from engine because test has completed
            // Now we'll be using the plugin in the project directory instead

            Logger.Log("Uninstall from Engine/Plugins/Marketplace");

            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);

            // Copy plugin to example project to prepare for package
            string exampleProjectPluginPath = Path.Combine(exampleProjectBuildPath, "Plugins", Path.GetFileName(plugin.GetPluginPath()));
            FileUtils.CopyDirectory(pluginBuildPath, exampleProjectPluginPath);

            Logger.Log("Packaging host project for demo");

            string demoPackagePath = Path.Combine(workingTempPath, @"DemoExe");

            FileUtils.DeleteDirectoryIfExists(demoPackagePath);

            PackageProject demoPackageOperation = new();
            OperationParameters demoPackageParams = new()
            {
                Target = new Project(exampleProjectBuildUProjectPath),
                OutputPathOverride = demoPackagePath
            };

            // Set options for demo exe
            demoPackageParams.RequestOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
            demoPackageParams.RequestOptions<PackageOptions>().NoDebugInfo.Value = true;

            OperationResult demoExePackageOperationResult = await demoPackageOperation.Execute(demoPackageParams, Logger, token);

            if (!demoExePackageOperationResult.Success)
            {
                throw new Exception("Example project build failed");
            }

            // Can't test package in shipping
            //OperationParameters demoTestParams = new OperationParameters()
            //{
            //    Target = new Package(Path.Combine(demoExePath, plugin.EngineInstall.GetWindowsPlatformName()))
            //};
            //demoTestParams.SetOptions(automationOpts);

            //if (!(await new LaunchPackage().Execute(demoTestParams, Logger)).Success)
            //{
            //    throw new Exception("Launch and test demo exe failed");
            //}

            {
                // Archiving

                Logger.Log("Archiving");

                string archivePath = Path.Combine(GetOutputPath(OperationParameters), "Archives");

                Directory.CreateDirectory(archivePath);

                // Archive plugin build

                string pluginBuildZipPath = Path.Combine(archivePath, archivePrefix + "PluginBuild.zip");
                bool archivePluginBuild = OperationParameters.RequestOptions<PluginDeployOptions>().ArchivePluginBuild;
                if (archivePluginBuild)
                {
                    Logger.Log("Archiving plugin build");
                    FileUtils.DeleteFileIfExists(pluginBuildZipPath);
                    ZipFile.CreateFromDirectory(pluginBuildPath, pluginBuildZipPath, CompressionLevel.Optimal, true);
                }

                // Archive demo exe

                string demoPackageZipPath = Path.Combine(archivePath, archivePrefix + "DemoPackage.zip");
                bool archiveDemoPackage = OperationParameters.RequestOptions<PluginDeployOptions>().ArchiveDemoPackage;
                if (archiveDemoPackage)
                {
                    Logger.Log("Archiving demo");

                    FileUtils.DeleteFileIfExists(demoPackageZipPath);
                    ZipFile.CreateFromDirectory(Path.Combine(demoPackagePath, plugin.EngineInstall.GetWindowsPlatformName()), demoPackageZipPath);
                }

                // Archive example project

                string[] allowedPluginFileExtensions = { ".uplugin" };
                string exampleProjectZipPath = Path.Combine(archivePath, archivePrefix + "ExampleProject.zip");
                bool archiveExampleProject = OperationParameters.RequestOptions<PluginDeployOptions>().ArchiveExampleProject;

                if (archiveExampleProject)
                {
                    Logger.Log("Archiving example project");

                    // First delete any extra directories
                    string[] allowedExampleProjectSubDirectoryNames = { "Content", "Config", "Plugins" };
                    FileUtils.DeleteOtherSubdirectories(exampleProjectBuildPath, allowedExampleProjectSubDirectoryNames);

                    var exampleProjectPlugins = exampleProjectBuild.GetPlugins();
                    string[] allowedExampleProjectPluginSubDirectoryNames = { "Content", "Config", "Binaries" };
                    string[] excludePlugins = OperationParameters.RequestOptions<PluginDeployOptions>().ExcludePlugins.Value.Replace(" ", "").Split(",");
                    foreach (Plugin exampleProjectPlugin in exampleProjectPlugins)
                    {
                        if (exampleProjectPlugin.Name == plugin.Name || !OperationParameters.RequestOptions<PluginDeployOptions>().IncludeOtherPlugins || excludePlugins.Contains(exampleProjectPlugin.Name))
                        {
                            // Delete target or excluded plugin from example project
                            FileUtils.DeleteDirectory(exampleProjectPlugin.TargetDirectory);
                        }
                        else
                        {
                            // Other plugins will be included, strip out unwanted files
                            FileUtils.DeleteOtherSubdirectories(exampleProjectPlugin.TargetDirectory, allowedExampleProjectPluginSubDirectoryNames);
                            FileUtils.DeleteFilesWithoutExtension(exampleProjectPlugin.TargetDirectory, allowedPluginFileExtensions);
                        }
                    }

                    // Delete debug files recursive
                    FileUtils.DeleteFilesWithExtension(exampleProjectBuildPath, new[] { ".pdb" }, SearchOption.AllDirectories);

                    FileUtils.DeleteFileIfExists(exampleProjectZipPath);
                    ZipFile.CreateFromDirectory(exampleProjectBuildPath, exampleProjectZipPath);
                }

                // Archive plugin for submission

                Logger.Log("Archiving plugin");

                // Copy plugin to submission folder so that we can prepare archive for submission without altering the original
                string pluginSubmissionPath = Path.Combine(workingTempPath, @"PluginSubmission", plugin.Name);
                FileUtils.DeleteDirectoryIfExists(pluginSubmissionPath);
                FileUtils.CopyDirectory(plugin.GetPluginPath(), pluginSubmissionPath);

                string[] allowedPluginSubmissionSubDirectoryNames = { "Source", "Resources", "Content", "Config" };
                FileUtils.DeleteOtherSubdirectories(pluginSubmissionPath, allowedPluginSubmissionSubDirectoryNames);

                // Delete top-level files other than uplugin
                FileUtils.DeleteFilesWithoutExtension(pluginSubmissionPath, allowedPluginFileExtensions);

                // Update .uplugin
                {
                    Plugin submissionPlugin = new(pluginSubmissionPath);
                    JObject submissionPluginDescriptor = JObject.Parse(File.ReadAllText(submissionPlugin.UPluginPath));
                    bool modified = false;

                    EngineInstallVersion desiredEngineMajorMinorVersion = engineVersion.WithPatch(0);

                    // Check version name
                    string desiredVersionName = $"{pluginVersionString}-{desiredEngineMajorMinorVersion.MajorMinorString}";
                    modified |= submissionPluginDescriptor.Set("VersionName", desiredVersionName);

                    // Check engine version
                    modified |= submissionPluginDescriptor.Set("EngineVersion", desiredEngineMajorMinorVersion.ToString());

                    if (modified)
                    {
                        File.WriteAllText(submissionPlugin.UPluginPath, submissionPluginDescriptor.ToString());
                    }
                }

                string pluginSubmissionZipPath = Path.Combine(archivePath, archivePrefix + "PluginSubmission.zip");
                FileUtils.DeleteFileIfExists(pluginSubmissionZipPath);
                ZipFile.CreateFromDirectory(pluginSubmissionPath, pluginSubmissionZipPath, CompressionLevel.Optimal, true);

                string archiveOutputPath = OperationParameters.RequestOptions<PluginDeployOptions>().ArchivePath;
                if (!string.IsNullOrEmpty(archiveOutputPath))
                {
                    Logger.Log("Copying to archive output path");
                    Directory.CreateDirectory(archiveOutputPath);
                    if (!Directory.Exists(archiveOutputPath))
                    {
                        throw new Exception($"Could not resolve archive output: {archiveOutputPath}");
                    }

                    FileUtils.CopyFile(pluginSubmissionZipPath, archiveOutputPath, true, true);

                    if (archivePluginBuild)
                    {
                        FileUtils.CopyFile(pluginBuildZipPath, archiveOutputPath, true, true);
                    }

                    if (archiveExampleProject)
                    {
                        FileUtils.CopyFile(exampleProjectZipPath, archiveOutputPath, true, true);
                    }

                    if (archiveDemoPackage)
                    {
                        FileUtils.CopyFile(demoPackageZipPath, archiveOutputPath, true, true);
                    }

                }

                Logger.Log("Finished archiving");
            }

            return new OperationResult(true);
        }

        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            operationParameters.RequestOptions<AutomationOptions>();
            operationParameters.RequestOptions<PluginBuildOptions>();
            operationParameters.RequestOptions<PluginDeployOptions>();
            return new List<Command>();
        }

        protected override bool FailOnWarning()
        {
            return true;
        }
    }
}