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
            Logger.Log("Preparing plugin");

            Plugin plugin = GetTarget(OperationParameters);
            PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
            Project hostProject = plugin.HostProject;
            ProjectDescriptor projectDescriptor = hostProject.ProjectDescriptor;

            if (!projectDescriptor.HasPluginEnabled(plugin.Name))
            {
                throw new Exception("Host project must have plugin enabled");
            }

            string branchName = VersionControlUtils.GetBranchName(hostProject.GetProjectPath());
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

            Logger.Log("On branch '" + branchName + "'");
            string archiveVersionName;
            if (pluginDescriptor.VersionName.Contains(branchName))
            {
                Logger.Log("Branch is contained in version name");
                archiveVersionName = pluginDescriptor.VersionName;
            }
            else if (!bStandardBranch)
            {
                Logger.Log("Branch isn't a version or standard branch");
                archiveVersionName = $"{pluginDescriptor.VersionName}-{branchName.Replace("/", "-")}";
            }
            else
            {
                Logger.Log("Branch is a version or standard branch");
                archiveVersionName = pluginDescriptor.VersionName;
            }
            Logger.Log("Archive version name is '" + archiveVersionName + "'");

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
            UnrealConfig config = new UnrealConfig(hostProjectDefaultGameConfig);
            string copyrightNotice = config.GetSection("/Script/EngineSettings.GeneralProjectSettings").GetValue("CopyrightNotice");

            string sourcePath = Path.Combine(plugin.TargetDirectory, "Source");
            string expectedComment = $"// {copyrightNotice}";
            foreach (string file in Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                string firstLine;
                using (StreamReader reader = new StreamReader(file))
                {
                    firstLine = reader.ReadLine();
                }
                if (firstLine != expectedComment)
                {
                    List<string> lines = File.ReadAllLines(file).ToList();
                    if (firstLine.StartsWith("//"))
                    {
                        // Replace existing comment with expected comment
                        lines[0] = expectedComment;
                    }
                    else
                    {
                        // Insert expected comment
                        lines.Insert(0, expectedComment);
                    }
                    File.WriteAllLines(file, lines);
                    string relativePath = System.IO.Path.GetRelativePath(sourcePath, file);
                    Logger.Log($"Updated copyright notice: {relativePath}");
                }
            }

            // Launch and test host project editor

            Logger.Log("Building host project editor");

            OperationParameters buildEditorParams = new()
            {
                Target = hostProject
            };

            if (!(await new BuildEditor().Execute(buildEditorParams, Logger, token)).Success)
            {
                throw new Exception("Failed to build host project");
            }

            AutomationOptions automationOpts = OperationParameters.FindOptions<AutomationOptions>();
            automationOpts.TestNameOverride = plugin.TestName;

            if (automationOpts.RunTests)
            {
                Logger.Log("Launching and testing host project editor");

                OperationParameters launchEditorParams = buildEditorParams;
                launchEditorParams.SetOptions(automationOpts);

                if (!(await new LaunchEditor().Execute(launchEditorParams, Logger, token)).Success)
                {
                    throw new Exception("Failed to launch host project");
                }
            }

            // Build plugin

            Logger.Log("Building plugin");

            string pluginBuildPath = Path.Combine(workingTempPath, @"PluginBuild", plugin.Name);
            FileUtils.DeleteDirectoryIfExists(pluginBuildPath);

            OperationParameters buildPluginParams = new OperationParameters()
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

            List<Plugin> sourcePlugins = hostProject.GetPlugins();
            foreach (Plugin sourcePlugin in sourcePlugins)
            {
                if (!sourcePlugin.Equals(plugin))
                {
                    FileUtils.CopyDirectory(sourcePlugin.TargetDirectory, Path.Combine(exampleProjectBuildPath, "Plugins"), true);
                }
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

            Project exampleProjectBuild = new Project(exampleProjectBuildUProjectPath);

            PackageProject installedPluginPackageOperation = new PackageProject();
            OperationParameters installedPluginPackageParams = new OperationParameters()
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

            if (automationOpts.RunTests)
            {
                OperationParameters testInstalledPluginBuildParams = new OperationParameters()
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

            string demoExePath = Path.Combine(workingTempPath, @"DemoExe");

            FileUtils.DeleteDirectoryIfExists(demoExePath);

            PackageProject demoPackageOperation = new PackageProject();
            OperationParameters demoPackageParams = new OperationParameters()
            {
                Target = new Project(exampleProjectBuildUProjectPath),
                OutputPathOverride = demoExePath
            };

            // Set options for demo exe
            demoPackageParams.RequestOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
            demoPackageParams.RequestOptions<PackageOptions>().NoDebugInfo.Value = true;

            OperationResult demoExePackageOperationResult = await demoPackageOperation.Execute(demoPackageParams, Logger, token);

            if (!demoExePackageOperationResult.Success)
            {
                throw new Exception("Example project build failed");
            }

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

                string archivePrefix = plugin.Name + "_" + archiveVersionName + "_";

                string archivePath = Path.Combine(GetOutputPath(OperationParameters), "Archives");

                Directory.CreateDirectory(archivePath);

                // Archive plugin build

                Logger.Log("Archiving plugin build");
                string pluginBuildZipPath = Path.Combine(archivePath, archivePrefix + "PluginBuild.zip");
                FileUtils.DeleteFileIfExists(pluginBuildZipPath);
                ZipFile.CreateFromDirectory(pluginBuildPath, pluginBuildZipPath, CompressionLevel.Optimal, true);

                // Archive demo exe

                Logger.Log("Archiving demo");

                string demoExeZipPath = Path.Combine(archivePath, archivePrefix + "DemoExe.zip");
                FileUtils.DeleteFileIfExists(demoExeZipPath);
                ZipFile.CreateFromDirectory(Path.Combine(demoExePath, plugin.EngineInstall.GetWindowsPlatformName()), demoExeZipPath);

                // Archive example project

                Logger.Log("Archiving example project");

                // First delete any extra directories
                string[] allowedExampleProjectSubDirectoryNames = { "Content", "Config", "Plugins" };
                FileUtils.DeleteOtherSubdirectories(exampleProjectBuildPath, allowedExampleProjectSubDirectoryNames);

                List<Plugin> exampleProjectPlugins = exampleProjectBuild.GetPlugins();
                string[] allowedExampleProjectPluginSubDirectoryNames = { "Content", "Config", "Binaries" };
                string[] allowedPluginFileExtensions = { ".uplugin" };
                foreach (Plugin exampleProjectPlugin in exampleProjectPlugins)
                {
                    if (exampleProjectPlugin.Name == plugin.Name)
                    {
                        // Delete target plugin from example project
                        FileUtils.DeleteDirectory(exampleProjectPlugin.TargetDirectory);
                    }
                    else
                    {
                        // Secondary plugins will be included, strip out unwanted files
                        FileUtils.DeleteOtherSubdirectories(exampleProjectPlugin.TargetDirectory, allowedExampleProjectPluginSubDirectoryNames);
                        FileUtils.DeleteFilesWithoutExtension(exampleProjectPlugin.TargetDirectory, allowedPluginFileExtensions);
                    }
                }

                // Delete debug files recursive
                FileUtils.DeleteFilesWithExtension(exampleProjectBuildPath, new[] { ".pdb" }, SearchOption.AllDirectories);

                string exampleProjectZipPath = Path.Combine(archivePath, archivePrefix + "ExampleProject.zip");
                FileUtils.DeleteFileIfExists(exampleProjectZipPath);
                ZipFile.CreateFromDirectory(exampleProjectBuildPath, exampleProjectZipPath);

                // Archive plugin for submission

                Logger.Log("Archiving plugin");

                // Copy plugin to submission folder so that we can prepare archive for submission without altering the original
                string pluginSubmissionPath = Path.Combine(workingTempPath, @"PluginSubmission", plugin.Name);
                FileUtils.DeleteDirectoryIfExists(pluginSubmissionPath);
                FileUtils.CopyDirectory(plugin.GetPluginPath(), pluginSubmissionPath);

                string[] allowedPluginSubmissionSubDirectoryNames = { "Source", "Resources", "Content", "Config" };
                FileUtils.DeleteOtherSubdirectories(pluginSubmissionPath, allowedPluginSubmissionSubDirectoryNames);

                // Delete top-level files other than uproject
                FileUtils.DeleteFilesWithoutExtension(pluginSubmissionPath, allowedPluginFileExtensions);

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
                    FileUtils.CopyFile(exampleProjectZipPath, archiveOutputPath, true, true);
                    FileUtils.CopyFile(pluginSubmissionZipPath, archiveOutputPath, true, true);
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
