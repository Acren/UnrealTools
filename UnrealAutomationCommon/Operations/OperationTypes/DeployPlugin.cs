﻿using Newtonsoft.Json.Linq;
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
    public class DeployPluginForEngine : Operation<Plugin>
    {
        public Engine Engine { get; set; }
        
        private Plugin Plugin { get; set; }
        
        private Project HostProject { get; set; }
        
        private Plugin BuiltPlugin { get; set; }
        
        private Project ExampleProject { get; set; }
        
        private Package DemoPackage { get; set; }
        
        private CancellationToken Token { get; set; }
        
        protected override async Task<OperationResult> OnExecuted(CancellationToken token)
        {
            Token = token;
            
            Engine engine = Engine;
            EngineVersion engineVersion = Engine.Version;
            Logger.Log($"Deploying plugin for {engineVersion.MajorMinorString}");

            Logger.LogSectionHeader("Preparing plugin");

            Plugin = GetTarget(OperationParameters);
            PluginDescriptor pluginDescriptor = Plugin.PluginDescriptor;
            HostProject = Plugin.HostProject;
            ProjectDescriptor projectDescriptor = HostProject.ProjectDescriptor;

            if (!projectDescriptor.HasPluginEnabled(Plugin.Name))
            {
                throw new Exception("Host project must have plugin enabled");
            }

            Logger.Log($"Engine version: {engineVersion}");

            bool bStandardBranch = true;
            string branchName = VersionControlUtils.GetBranchName(HostProject.ProjectPath);
            if (!string.IsNullOrEmpty(branchName))
            {
                Logger.Log($"Branch: {branchName}");
                // Use the version if on any of these branches
                string[] standardBranchNames = { "master", "develop", "development" };
                string[] standardBranchPrefixes = { "version/", "release/", "hotfix/" };

                bStandardBranch = standardBranchNames.Contains(branchName, StringComparer.InvariantCultureIgnoreCase);
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
            }

            string archivePrefix = Plugin.Name;

            bool beta = pluginDescriptor.IsBetaVersion;
            if (beta)
            {
                Logger.Log("Plugin is marked as beta version");
                archivePrefix += "_beta";
            }

            string pluginVersionString = pluginDescriptor.VersionName;
            string fullPluginVersionString = pluginVersionString;

            if (!string.IsNullOrEmpty(branchName))
            {
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
            }

            archivePrefix += $"_{fullPluginVersionString}";
            archivePrefix += $"_UE{engineVersion.MajorMinorString}";
            archivePrefix += "_";

            Logger.Log($"Archive name prefix is '{archivePrefix}'");

            // Get engine path

            string enginePath = engine.TargetPath;

            string enginePluginsMarketplacePath = Path.Combine(enginePath, @"Engine\Plugins\Marketplace");
            string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, Plugin.Name);

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

            // Update .uplugin version if required
            int version = Plugin.PluginDescriptor.SemVersion.ToInt();
            Logger.Log($"Version '{Plugin.PluginDescriptor.VersionName}' -> {version}");
            bool updated = Plugin.UpdateVersionInteger();
            Logger.Log(updated ? "Updated .uplugin version from name" : ".uplugin already has correct version");

            // Check copyright notice
            string copyrightNotice = HostProject.GetCopyrightNotice();

            string sourcePath = Path.Combine(Plugin.TargetDirectory, "Source");
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
            
            AutomationOptions automationOptions = OperationParameters.FindOptions<AutomationOptions>();
            automationOptions.TestNameOverride = Plugin.TestName;

            // Build host project editor

            await BuildEditor();

            // Launch and test host project editor

            await TestEditor(automationOptions);

            // Launch and test standalone

            await TestStandalone(automationOptions);

            // Build plugin

            await BuildPlugin();

            // Set up example project

            await PrepareExampleProject();

            await BuildCodeExampleProject();

            // Package code example project with plugin inside project

            await TestCodeExampleProjectWithProjectPlugin(automationOptions);

            // Copy plugin into engine where the marketplace installs it

            await TestCodeExampleProjectWithEnginePlugin(automationOptions);

            // Package blueprint-only example project with plugin installed to engine

            await PackageBlueprintExampleProjectWithEnginePlugin();

            await PackageDemoExecutable();

            // Archiving
            await ArchiveArtifacts(archivePrefix);

            Logger.Log($"Finished deploying plugin for {engineVersion.MajorMinorString}");

            return new OperationResult(true);
        }

        protected override IEnumerable<Command> BuildCommands(OperationParameters operationParameters)
        {
            return new List<Command>();
        }

        private async Task BuildEditor()
        {
            Logger.LogSectionHeader("Building host project editor");

            OperationParameters buildEditorParams = new()
            {
                Target = HostProject,
                EngineOverride = Engine
            };

            if (!(await new BuildEditor().Execute(buildEditorParams, Logger, Token)).Success)
            {
                throw new Exception("Failed to build host project editor");
            }
        }

        private async Task TestEditor(AutomationOptions automationOptions)
        {
            if (automationOptions.RunTests)
            {
                Logger.LogSectionHeader("Launching and testing host project editor");

                OperationParameters launchEditorParams = new()
                {
                    Target = HostProject,
                    EngineOverride = Engine
                };
                launchEditorParams.SetOptions(automationOptions);

                if (!(await new LaunchProjectEditor().Execute(launchEditorParams, Logger, Token)).Success)
                {
                    throw new Exception("Failed to launch host project");
                }
            }
        }

        private async Task TestStandalone(AutomationOptions automationOptions)
        {
            if (automationOptions.RunTests && OperationParameters.RequestOptions<PluginDeployOptions>().TestStandalone)
            {
                Logger.LogSectionHeader("Launching and testing standalone");

                OperationParameters launchStandaloneParams = new()
                {
                    Target = HostProject,
                    EngineOverride = Engine
                };
                launchStandaloneParams.SetOptions(automationOptions);

                if (!(await new LaunchStandalone().Execute(launchStandaloneParams, Logger, Token)).Success)
                {
                    throw new Exception("Failed to launch standalone");
                }
            }
        }

        private async Task BuildPlugin()
        {
            Logger.LogSectionHeader("Building plugin");

            string pluginBuildPath = Path.Combine(GetOperationTempPath(), @"PluginBuild", Plugin.Name);
            FileUtils.DeleteDirectoryIfExists(pluginBuildPath);

            OperationParameters buildPluginParams = new()
            {
                Target = Plugin,
                EngineOverride = Engine,
                OutputPathOverride = pluginBuildPath
            };
            buildPluginParams.SetOptions(OperationParameters.RequestOptions<PluginBuildOptions>());

            OperationResult buildResult = await new BuildPlugin().Execute(buildPluginParams, Logger, Token);

            if (!buildResult.Success)
            {
                throw new Exception("Plugin build failed");
            }
            
            BuiltPlugin = new Plugin(pluginBuildPath);

            Logger.Log("Plugin build complete");
        }

        private async Task PrepareExampleProject()
        {
            Logger.LogSectionHeader("Preparing host project");

            string uProjectFilename = Path.GetFileName(HostProject.UProjectPath);
            string projectName = Path.GetFileNameWithoutExtension(HostProject.UProjectPath);

            string exampleProjectPath = Path.Combine(GetOperationTempPath(), @"ExampleProject");

            FileUtils.DeleteDirectoryIfExists(exampleProjectPath);

            FileUtils.CopySubdirectory(HostProject.ProjectPath, exampleProjectPath, "Content");
            FileUtils.CopySubdirectory(HostProject.ProjectPath, exampleProjectPath, "Config");
            FileUtils.CopySubdirectory(HostProject.ProjectPath, exampleProjectPath, "Source");

            string projectIcon = projectName + ".png";
            if (File.Exists(projectIcon))
            {
                FileUtils.CopyFile(HostProject.ProjectPath, exampleProjectPath, projectIcon);
            }

            // Copy uproject 
            JObject uProjectContents = JObject.Parse(File.ReadAllText(HostProject.UProjectPath));

            string exampleProjectBuildUProjectPath = Path.Combine(exampleProjectPath, uProjectFilename);

            File.WriteAllText(exampleProjectBuildUProjectPath, uProjectContents.ToString());

            ExampleProject = new(exampleProjectPath);

            // Copy other plugins

            var sourcePlugins = HostProject.Plugins;
            foreach (Plugin sourcePlugin in sourcePlugins)
            {
                if (!sourcePlugin.Equals(Plugin))
                {
                    ExampleProject.AddPlugin(sourcePlugin);
                }
            }

            // Copy built plugin to example project
            ExampleProject.AddPlugin(BuiltPlugin);
        }

        private async Task BuildCodeExampleProject()
        {
            Logger.Log("Building example project with modules");
            // Note: Modules and source are required to build any code plugins that are used for testing

            OperationParameters buildExampleProjectParams = new()
            {
                Target = ExampleProject,
                EngineOverride = Engine
            };
            OperationResult exampleProjectBuildResult = await new BuildEditor().Execute(buildExampleProjectParams, Logger, Token);
            if (!exampleProjectBuildResult.Success)
            {
                throw new Exception($"Failed to build example project with modules");
            }
        }

        private async Task TestCodeExampleProjectWithProjectPlugin(AutomationOptions automationOptions)
        {
            Logger.LogSectionHeader("Packaging code example project with plugin inside project");

            string projectPluginPackagePath = Path.Combine(GetOperationTempPath(), @"ProjectPluginPackage");
            FileUtils.DeleteDirectoryIfExists(projectPluginPackagePath);

            OperationParameters packageWithPluginParams = new()
            {
                Target = ExampleProject,
                EngineOverride = Engine,
                OutputPathOverride = projectPluginPackagePath
            };

            OperationResult buildWithProjectPluginResult = await new PackageProject().Execute(packageWithPluginParams, Logger, Token);

            if (!buildWithProjectPluginResult.Success)
            {
                throw new Exception("Package project with included plugin failed");
            }
            
            if (automationOptions.RunTests && OperationParameters.RequestOptions<PluginDeployOptions>().TestPackageWithProjectPlugin)
            {
                Logger.LogSectionHeader("Testing code project package with project plugin");

                Package projectPluginPackage = new (Path.Combine(projectPluginPackagePath, Engine.GetWindowsPlatformName()));

                OperationParameters testProjectPluginPackageParams = new()
                {
                    Target = projectPluginPackage,
                    EngineOverride = Engine
                };
                testProjectPluginPackageParams.SetOptions(automationOptions);

                OperationResult testResult = await new LaunchPackage().Execute(testProjectPluginPackageParams, Logger, Token);

                if (!testResult.Success)
                {
                    throw new Exception("Launch and test with project plugin failed");
                }
            }
        }

        private async Task TestCodeExampleProjectWithEnginePlugin(AutomationOptions automationOptions)
        {
            Logger.LogSectionHeader("Preparing to package example project with installed plugin");
            
            string enginePluginsMarketplacePath = Path.Combine(Engine.TargetPath, @"Engine\Plugins\Marketplace");
            string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, Plugin.Name);

            Logger.Log($"Copying plugin to {enginePluginsMarketplacePluginPath}");

            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);

            FileUtils.CopyDirectory(BuiltPlugin.PluginPath, enginePluginsMarketplacePluginPath);

            // Package code example project with plugin installed to engine
            // It's worth doing this to test for build or packaging issues that might only happen using installed plugin

            Logger.LogSectionHeader("Packaging code example project with installed plugin");

            // Remove the plugin in the project because it should only be in the engine
            ExampleProject.RemovePlugin(Plugin.Name);

            string enginePluginPackagePath = Path.Combine(GetOperationTempPath(), @"EnginePluginPackage");
            FileUtils.DeleteDirectoryIfExists(enginePluginPackagePath);

            OperationParameters installedPluginPackageParams = new()
            {
                Target = ExampleProject,
                EngineOverride = Engine,
                OutputPathOverride = enginePluginPackagePath
            };

            OperationResult installedPluginPackageOperationResult = await new PackageProject().Execute(installedPluginPackageParams, Logger, Token);

            if (!installedPluginPackageOperationResult.Success)
            {
                throw new Exception("Package project with engine plugin failed");
            }

            // Test the package
            if (automationOptions.RunTests && OperationParameters.RequestOptions<PluginDeployOptions>().TestPackageWithEnginePlugin)
            {
                Logger.LogSectionHeader("Testing code project package with installed plugin");

                Package enginePluginPackage = new Package(Path.Combine(enginePluginPackagePath, Engine.GetWindowsPlatformName()));

                OperationParameters testEnginePluginPackageParams = new()
                {
                    Target = enginePluginPackage,
                    EngineOverride = Engine
                };
                testEnginePluginPackageParams.SetOptions(automationOptions);

                OperationResult testResult = await new LaunchPackage().Execute(testEnginePluginPackageParams, Logger, Token);

                if (!testResult.Success)
                {
                    throw new Exception("Launch and test with installed plugin failed");
                }
            }
        }

        private async Task PackageBlueprintExampleProjectWithEnginePlugin()
        {
            Logger.LogSectionHeader("Packaging blueprint-only example project");

            ExampleProject.ConvertToBlueprintOnly();

            string blueprintOnlyPackagePath = Path.Combine(GetOperationTempPath(), @"BlueprintOnlyPackage");
            FileUtils.DeleteDirectoryIfExists(blueprintOnlyPackagePath);

            OperationParameters blueprintOnlyPackageParams = new()
            {
                Target = ExampleProject,
                EngineOverride = Engine,
                OutputPathOverride = blueprintOnlyPackagePath
            };

            OperationResult blueprintOnlyPackageOperationResult = await new PackageProject().Execute(blueprintOnlyPackageParams, Logger, Token);

            if (!blueprintOnlyPackageOperationResult.Success)
            {
                throw new Exception("Package blueprint-only project failed");
            }
        }

        private async Task PackageDemoExecutable()
        {
            // If true, the demo executable will be packaged with the plugin installed to the project
            // This is currently disabled because in 5.3 blueprint-only projects will fail to load plugins that are installed to the project
            bool packageDemoExecutableWithProjectPlugin = false;

            if (packageDemoExecutableWithProjectPlugin)
            {
                // Uninstall plugin from engine because test has completed
                // Now we'll be using the plugin in the project directory instead

                Logger.Log("Uninstall from Engine/Plugins/Marketplace");
                
                Engine.UninstallPlugin(Plugin.Name);

                // Copy plugin to example project to prepare the demo package
                string exampleProjectPluginPath = Path.Combine(ExampleProject.ProjectPath, "Plugins", Path.GetFileName(Plugin.PluginPath));
                FileUtils.CopyDirectory(BuiltPlugin.PluginPath, exampleProjectPluginPath);
            }

            // Package demo executable

            Logger.LogSectionHeader("Packaging host project for demo");

            string demoPackagePath = Path.Combine(GetOperationTempPath(), @"DemoExe");

            FileUtils.DeleteDirectoryIfExists(demoPackagePath);

            PackageProject demoPackageOperation = new();
            OperationParameters demoPackageParams = new()
            {
                Target = ExampleProject,
                EngineOverride = Engine,
                OutputPathOverride = demoPackagePath
            };

            // Set options for demo exe
            demoPackageParams.RequestOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
            demoPackageParams.RequestOptions<PackageOptions>().NoDebugInfo.Value = true;

            OperationResult demoExePackageOperationResult = await demoPackageOperation.Execute(demoPackageParams, Logger, Token);

            if (!demoExePackageOperationResult.Success)
            {
                throw new Exception("Example project build failed");
            }

            DemoPackage = new Package(demoPackagePath);

            // Can't test the demo package in shipping
        }

        private async Task ArchiveArtifacts(string archivePrefix)
        {
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
                ZipFile.CreateFromDirectory(BuiltPlugin.PluginPath, pluginBuildZipPath, CompressionLevel.Optimal, true);
            }

            // Archive demo exe

            string demoPackageZipPath = Path.Combine(archivePath, archivePrefix + "DemoPackage.zip");
            bool archiveDemoPackage = OperationParameters.RequestOptions<PluginDeployOptions>().ArchiveDemoPackage;
            if (archiveDemoPackage)
            {
                Logger.Log("Archiving demo");

                FileUtils.DeleteFileIfExists(demoPackageZipPath);
                ZipFile.CreateFromDirectory(Path.Combine(DemoPackage.TargetPath, Engine.GetWindowsPlatformName()), demoPackageZipPath);
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
                FileUtils.DeleteOtherSubdirectories(ExampleProject.ProjectPath, allowedExampleProjectSubDirectoryNames);

                var exampleProjectPlugins = ExampleProject.Plugins;
                string[] allowedExampleProjectPluginSubDirectoryNames = { "Content", "Config", "Binaries" };
                string[] excludePlugins = OperationParameters.RequestOptions<PluginDeployOptions>().ExcludePlugins.Value.Replace(" ", "").Split(",");
                foreach (Plugin exampleProjectPlugin in exampleProjectPlugins)
                {
                    if (exampleProjectPlugin.Name == Plugin.Name || !OperationParameters.RequestOptions<PluginDeployOptions>().IncludeOtherPlugins || excludePlugins.Contains(exampleProjectPlugin.Name))
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
                FileUtils.DeleteFilesWithExtension(ExampleProject.ProjectPath, new[] { ".pdb" }, SearchOption.AllDirectories);

                FileUtils.DeleteFileIfExists(exampleProjectZipPath);
                ZipFile.CreateFromDirectory(ExampleProject.ProjectPath, exampleProjectZipPath);
            }

            // Archive plugin source for submission

            Logger.Log("Archiving plugin source");

            // Copy plugin to working folder so that we can prepare archive without altering the original
            string pluginSourcePath = Path.Combine(GetOperationTempPath(), @"PluginSource", Plugin.Name);
            FileUtils.DeleteDirectoryIfExists(pluginSourcePath);
            FileUtils.CopyDirectory(Plugin.PluginPath, pluginSourcePath);

            string[] allowedPluginSourceArchiveSubDirectoryNames = { "Source", "Resources", "Content", "Config" };
            FileUtils.DeleteOtherSubdirectories(pluginSourcePath, allowedPluginSourceArchiveSubDirectoryNames);

            // Delete top-level files other than uplugin
            FileUtils.DeleteFilesWithoutExtension(pluginSourcePath, allowedPluginFileExtensions);

            // Update .uplugin
            {
                Plugin sourceArchivePlugin = new(pluginSourcePath);
                JObject sourceArchivePluginDescriptor = JObject.Parse(File.ReadAllText(sourceArchivePlugin.UPluginPath));
                bool modified = false;

                EngineVersion desiredEngineMajorMinorVersion = Engine.Version.WithPatch(0);

                // Check version name
                string desiredVersionName = $"{Plugin.PluginDescriptor.VersionName}-{desiredEngineMajorMinorVersion.MajorMinorString}";
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
    }
    
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
            DeployPluginForEngine deployForEngineOp = new() { Engine = engine };
            return await deployForEngineOp.Execute(OperationParameters, Logger, token);
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