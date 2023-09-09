using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class DeployPlugin : Operation<Plugin>
    {
        protected override async Task<OperationResult> OnExecuted(CancellationToken token)
        {
            Plugin plugin = GetTarget(OperationParameters);

            Logger.Log($"Versions: {string.Join(", ", OperationParameters.RequestOptions<EngineVersionOptions>().EnabledVersions.Value.Select(x => x.MajorMinorString)) }");
            foreach (EngineVersion engineVersion in OperationParameters.RequestOptions<EngineVersionOptions>().EnabledVersions.Value)
            {
                Engine engine = EngineFinder.GetEngineInstall(engineVersion);
                if (engine == null)
                {
                    throw new Exception("Engine not found");
                }
                OperationResult result = await DeployForEngine(engine, token);
                if (!result.Success)
                {
                    // Failure
                    return result;
                }
            }

            return new OperationResult(true);
        }

        private async Task<OperationResult> DeployForEngine(Engine engine, CancellationToken token)
        {
            EngineVersion engineVersion = engine.Version;
            Logger.Log($"Deploying plugin for {engineVersion.MajorMinorString}");

            Logger.LogSectionHeader("Preparing plugin");

            Plugin plugin = GetTarget(OperationParameters);
            PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
            Project hostProject = plugin.HostProject;
            ProjectDescriptor projectDescriptor = hostProject.ProjectDescriptor;

            if (!projectDescriptor.HasPluginEnabled(plugin.Name))
            {
                throw new Exception("Host project must have plugin enabled");
            }

            Logger.Log($"Engine version: {engineVersion}");

            string branchName = VersionControlUtils.GetBranchName(hostProject.ProjectPath);
            Logger.Log($"Branch: {branchName}");
            // Use the version if on any of these branches
            string[] standardBranchNames = { "master", "develop", "development" };
            string[] standardBranchPrefixes = { "version/", "release/", "hotfix/" };

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

            string enginePath = engine.TargetPath;

            string enginePluginsMarketplacePath = Path.Combine(enginePath, @"Engine\Plugins\Marketplace");
            string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, plugin.Name);

            // Delete plugin from engine if installed version exists
            Policy policy = Policy
                .Handle<UnauthorizedAccessException>()
                .RetryForever((ex, retryAttempt, ctx) =>
                {
                    Logger.Log(ex.ToString());
                    OperationParameters.RetryHandler(ex);
                });
            policy.Execute(() => { FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath); });

            string workingTempPath = GetOperationTempPath();

            Directory.CreateDirectory(workingTempPath);

            // Check copyright notice
            string copyrightNotice = hostProject.GetCopyrightNotice();

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

            Logger.LogSectionHeader("Building host project editor");

            OperationParameters buildEditorParams = new()
            {
                Target = hostProject,
                EngineOverride = engine
            };

            if (!(await new BuildEditor().Execute(buildEditorParams, Logger, token)).Success)
            {
                throw new Exception("Failed to build host project editor");
            }

            // Launch and test host project editor

            AutomationOptions automationOpts = OperationParameters.FindOptions<AutomationOptions>();
            automationOpts.TestNameOverride = plugin.TestName;

            if (automationOpts.RunTests)
            {
                Logger.LogSectionHeader("Launching and testing host project editor");

                OperationParameters launchEditorParams = new()
                {
                    Target = hostProject,
                    EngineOverride = engine
                };
                launchEditorParams.SetOptions(automationOpts);

                if (!(await new LaunchProjectEditor().Execute(launchEditorParams, Logger, token)).Success)
                {
                    throw new Exception("Failed to launch host project");
                }
            }

            // Launch and test standalone

            if (automationOpts.RunTests && OperationParameters.RequestOptions<PluginDeployOptions>().TestStandalone)
            {
                Logger.LogSectionHeader("Launching and testing standalone");

                OperationParameters launchStandaloneParams = new()
                {
                    Target = hostProject,
                    EngineOverride = engine
                };
                launchStandaloneParams.SetOptions(automationOpts);

                if (!(await new LaunchStandalone().Execute(launchStandaloneParams, Logger, token)).Success)
                {
                    throw new Exception("Failed to launch standalone");
                }
            }

            // Build plugin

            Logger.LogSectionHeader("Building plugin");

            string pluginBuildPath = Path.Combine(workingTempPath, @"PluginBuild", plugin.Name);
            FileUtils.DeleteDirectoryIfExists(pluginBuildPath);

            OperationParameters buildPluginParams = new()
            {
                Target = plugin,
                EngineOverride = engine,
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

            // Set up host project

            Logger.LogSectionHeader("Preparing host project");

            string uProjectFilename = Path.GetFileName(hostProject.UProjectPath);
            string projectName = Path.GetFileNameWithoutExtension(hostProject.UProjectPath);

            string exampleProjectPath = Path.Combine(workingTempPath, @"ExampleProject");

            FileUtils.DeleteDirectoryIfExists(exampleProjectPath);

            FileUtils.CopySubdirectory(hostProject.ProjectPath, exampleProjectPath, "Content");
            FileUtils.CopySubdirectory(hostProject.ProjectPath, exampleProjectPath, "Config");
            FileUtils.CopySubdirectory(hostProject.ProjectPath, exampleProjectPath, "Source");

            string projectIcon = projectName + ".png";
            if (File.Exists(projectIcon))
            {
                FileUtils.CopyFile(hostProject.ProjectPath, exampleProjectPath, projectIcon);
            }

            // Copy uproject 
            JObject uProjectContents = JObject.Parse(File.ReadAllText(hostProject.UProjectPath));

            string exampleProjectBuildUProjectPath = Path.Combine(exampleProjectPath, uProjectFilename);

            File.WriteAllText(exampleProjectBuildUProjectPath, uProjectContents.ToString());

            Project exampleProject = new(exampleProjectPath);

            // Copy other plugins

            var sourcePlugins = hostProject.Plugins;
            foreach (Plugin sourcePlugin in sourcePlugins)
            {
                if (!sourcePlugin.Equals(plugin))
                {
                    exampleProject.AddPlugin(sourcePlugin);
                }
            }

            // Copy main plugin to example project
            exampleProject.AddPlugin(pluginBuildPath);

            Logger.Log("Building example project with modules");
            // Note: Modules and source are required to build any code plugins that are used for testing

            OperationParameters buildExampleProjectParams = new()
            {
                Target = exampleProject,
                EngineOverride = engine
            };
            OperationResult exampleProjectBuildResult = await new BuildEditor().Execute(buildExampleProjectParams, Logger, token);
            if (!exampleProjectBuildResult.Success)
            {
                throw new Exception($"Failed to build example project with modules");
            }

            // Package code example project with plugin inside project

            Logger.LogSectionHeader("Packaging code example project with plugin inside project");

            string projectPluginPackagePath = Path.Combine(workingTempPath, @"ProjectPluginPackage");
            FileUtils.DeleteDirectoryIfExists(projectPluginPackagePath);

            OperationParameters packageWithPluginParams = new()
            {
                Target = exampleProject,
                EngineOverride = engine,
                OutputPathOverride = projectPluginPackagePath
            };

            OperationResult buildWithProjectPluginResult = await new PackageProject().Execute(packageWithPluginParams, Logger, token);

            if (!buildWithProjectPluginResult.Success)
            {
                throw new Exception("Package project with included plugin failed");
            }

            if (automationOpts.RunTests && OperationParameters.RequestOptions<PluginDeployOptions>().TestPackageWithProjectPlugin)
            {
                Logger.LogSectionHeader("Testing code project package with project plugin");

                Package projectPluginPackage = new (Path.Combine(projectPluginPackagePath, engine.GetWindowsPlatformName()));

                OperationParameters testProjectPluginPackageParams = new()
                {
                    Target = projectPluginPackage,
                    EngineOverride = engine
                };
                testProjectPluginPackageParams.SetOptions(automationOpts);

                OperationResult testResult = await new LaunchPackage().Execute(testProjectPluginPackageParams, Logger, token);

                if (!testResult.Success)
                {
                    throw new Exception("Launch and test with project plugin failed");
                }
            }

            // Copy plugin into engine where the marketplace installs it

            Logger.LogSectionHeader("Preparing to package example project with installed plugin");

            Logger.Log($"Copying plugin to {enginePluginsMarketplacePluginPath}");

            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);

            FileUtils.CopyDirectory(pluginBuildPath, enginePluginsMarketplacePluginPath);

            // Package code example project with plugin installed to engine
            // It's worth doing this to test for build or packaging issues that might only happen using installed plugin

            Logger.LogSectionHeader("Packaging code example project with installed plugin");

            // Remove the plugin in the project because it should only be in the engine
            exampleProject.RemovePlugin(plugin.Name);

            string enginePluginPackagePath = Path.Combine(workingTempPath, @"EnginePluginPackage");
            FileUtils.DeleteDirectoryIfExists(enginePluginPackagePath);

            OperationParameters installedPluginPackageParams = new()
            {
                Target = exampleProject,
                EngineOverride = engine,
                OutputPathOverride = enginePluginPackagePath
            };

            OperationResult installedPluginPackageOperationResult = await new PackageProject().Execute(installedPluginPackageParams, Logger, token);

            if (!installedPluginPackageOperationResult.Success)
            {
                throw new Exception("Package project with engine plugin failed");
            }

            // Test the package

            if (automationOpts.RunTests && OperationParameters.RequestOptions<PluginDeployOptions>().TestPackageWithEnginePlugin)
            {
                Logger.LogSectionHeader("Testing code project package with installed plugin");

                Package enginePluginPackage = new Package(Path.Combine(enginePluginPackagePath, engine.GetWindowsPlatformName()));

                OperationParameters testEnginePluginPackageParams = new()
                {
                    Target = enginePluginPackage,
                    EngineOverride = engine
                };
                testEnginePluginPackageParams.SetOptions(automationOpts);

                OperationResult testResult = await new LaunchPackage().Execute(testEnginePluginPackageParams, Logger, token);

                if (!testResult.Success)
                {
                    throw new Exception("Launch and test with installed plugin failed");
                }
            }

            // Package blueprint-only example project with plugin installed to engine

            Logger.LogSectionHeader("Packaging blueprint-only example project");

            exampleProject.ConvertToBlueprintOnly();

            string blueprintOnlyPackagePath = Path.Combine(workingTempPath, @"BlueprintOnlyPackage");
            FileUtils.DeleteDirectoryIfExists(blueprintOnlyPackagePath);

            OperationParameters blueprintOnlyPackageParams = new()
            {
                Target = exampleProject,
                EngineOverride = engine,
                OutputPathOverride = blueprintOnlyPackagePath
            };

            OperationResult blueprintOnlyPackageOperationResult = await new PackageProject().Execute(blueprintOnlyPackageParams, Logger, token);

            if (!blueprintOnlyPackageOperationResult.Success)
            {
                throw new Exception("Package blueprint-only project failed");
            }

            // Uninstall plugin from engine because test has completed
            // Now we'll be using the plugin in the project directory instead

            Logger.Log("Uninstall from Engine/Plugins/Marketplace");

            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);

            // Copy plugin to example project to prepare the demo package
            string exampleProjectPluginPath = Path.Combine(exampleProjectPath, "Plugins", Path.GetFileName(plugin.PluginPath));
            FileUtils.CopyDirectory(pluginBuildPath, exampleProjectPluginPath);

            Logger.LogSectionHeader("Packaging host project for demo");

            string demoPackagePath = Path.Combine(workingTempPath, @"DemoExe");

            FileUtils.DeleteDirectoryIfExists(demoPackagePath);

            PackageProject demoPackageOperation = new();
            OperationParameters demoPackageParams = new()
            {
                Target = new Project(exampleProjectPath),
                EngineOverride = engine,
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

            // Can't test the demo package in shipping
            {
                // Archiving

                Logger.LogSectionHeader("Archiving");

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
                    ZipFile.CreateFromDirectory(Path.Combine(demoPackagePath, engine.GetWindowsPlatformName()), demoPackageZipPath);
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
                    FileUtils.DeleteOtherSubdirectories(exampleProjectPath, allowedExampleProjectSubDirectoryNames);

                    var exampleProjectPlugins = exampleProject.Plugins;
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
                    FileUtils.DeleteFilesWithExtension(exampleProjectPath, new[] { ".pdb" }, SearchOption.AllDirectories);

                    FileUtils.DeleteFileIfExists(exampleProjectZipPath);
                    ZipFile.CreateFromDirectory(exampleProjectPath, exampleProjectZipPath);
                }

                // Archive plugin source for submission

                Logger.Log("Archiving plugin source");

                // Copy plugin to working folder so that we can prepare archive without altering the original
                string pluginSourcePath = Path.Combine(workingTempPath, @"PluginSource", plugin.Name);
                FileUtils.DeleteDirectoryIfExists(pluginSourcePath);
                FileUtils.CopyDirectory(plugin.PluginPath, pluginSourcePath);

                string[] allowedPluginSourceArchiveSubDirectoryNames = { "Source", "Resources", "Content", "Config" };
                FileUtils.DeleteOtherSubdirectories(pluginSourcePath, allowedPluginSourceArchiveSubDirectoryNames);

                // Delete top-level files other than uplugin
                FileUtils.DeleteFilesWithoutExtension(pluginSourcePath, allowedPluginFileExtensions);

                // Update .uplugin
                {
                    Plugin sourceArchivePlugin = new(pluginSourcePath);
                    JObject sourceArchivePluginDescriptor = JObject.Parse(File.ReadAllText(sourceArchivePlugin.UPluginPath));
                    bool modified = false;

                    EngineVersion desiredEngineMajorMinorVersion = engineVersion.WithPatch(0);

                    // Check version name
                    string desiredVersionName = $"{pluginVersionString}-{desiredEngineMajorMinorVersion.MajorMinorString}";
                    modified |= sourceArchivePluginDescriptor.Set("VersionName", desiredVersionName);

                    // Check engine version
                    modified |= sourceArchivePluginDescriptor.Set("EngineVersion", desiredEngineMajorMinorVersion.ToString());

                    if (modified)
                    {
                        File.WriteAllText(sourceArchivePlugin.UPluginPath, sourceArchivePluginDescriptor.ToString());
                    }
                }

                string pluginSourceArchiveZipPath = Path.Combine(archivePath, archivePrefix + "PluginSource.zip");
                FileUtils.DeleteFileIfExists(pluginSourceArchiveZipPath);
                ZipFile.CreateFromDirectory(pluginSourcePath, pluginSourceArchiveZipPath, CompressionLevel.Optimal, true);

                string archiveOutputPath = OperationParameters.RequestOptions<PluginDeployOptions>().ArchivePath;
                if (!string.IsNullOrEmpty(archiveOutputPath))
                {
                    Logger.Log("Copying to archive output path");
                    Directory.CreateDirectory(archiveOutputPath);
                    if (!Directory.Exists(archiveOutputPath))
                    {
                        throw new Exception($"Could not resolve archive output: {archiveOutputPath}");
                    }

                    FileUtils.CopyFile(pluginSourceArchiveZipPath, archiveOutputPath, true, true);

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

            Logger.Log($"Finished deploying plugin for {engineVersion.MajorMinorString}");

            return new OperationResult(true);
        }

        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            operationParameters.RequestOptions<EngineVersionOptions>();
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