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
using Microsoft.Extensions.Logging;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class DeployPluginForEngine : UnrealOperation<Plugin>
    {
        // This operation is populated in phases by the deployment pipeline, so these members are assigned before the
        // corresponding step uses them even though construction happens earlier.
        public Engine Engine { get; set; } = null!;
        
        private Plugin Plugin { get; set; } = null!;
        
        private Project HostProject { get; set; } = null!;
        
        private Plugin BuiltPlugin { get; set; } = null!;
        
        private Project ExampleProject { get; set; } = null!;
        
        private Package DemoPackage { get; set; } = null!;
        
        private CancellationToken Token { get; set; }
        
        private string[] _allowedPluginFileExtensions = { ".uplugin" };
        
        private Plugin StagingPlugin { get; set; } = null!;

        private void UpdatePluginDescriptorForArchive(Plugin plugin)
        {
            Engine engine = Engine;
            Plugin sourcePlugin = Plugin;
            EngineVersion engineVersion = engine.Version;
            PluginDescriptor pluginDescriptorModel = sourcePlugin.PluginDescriptor;
            JObject pluginDescriptor = JObject.Parse(File.ReadAllText(plugin.UPluginPath));
            bool modified = false;

            // Check version name - use same format as example project
            string desiredVersionName = ProjectConfig.BuildVersionWithEnginePrefix(pluginDescriptorModel.VersionName, engineVersion);
            modified |= pluginDescriptor.Set("VersionName", desiredVersionName);

            // Check engine version
            EngineVersion desiredEngineMajorMinorVersion = engineVersion.WithPatch(0);
            modified |= pluginDescriptor.Set("EngineVersion", desiredEngineMajorMinorVersion.ToString());

            if (modified)
            {
                File.WriteAllText(plugin.UPluginPath, pluginDescriptor.ToString());
            }
        }
        
        private void UpdateProjectDescriptorForArchive(Project project)
        {
            Engine engine = Engine;
            JObject projectDescriptor = JObject.Parse(File.ReadAllText(project.UProjectPath));
            bool modified = false;

            // Check engine association - use major.minor format
            string desiredEngineAssociation = engine.Version.MajorMinorString;
            modified |= projectDescriptor.Set("EngineAssociation", desiredEngineAssociation);

            if (modified)
            {
                File.WriteAllText(project.UProjectPath, projectDescriptor.ToString());
            }
        }
      
        protected override async Task<global::LocalAutomation.Runtime.OperationResult> OnExecuted(CancellationToken token)
        {
            Token = token;

            try
            {
                Engine engine = Engine;
                EngineVersion engineVersion = engine.Version;
                Logger.LogInformation($"Deploying plugin for {engineVersion.MajorMinorString}");

                string archivePrefix;
                using (Logger.BeginSection("Preparing plugin"))
                {
                    Plugin = GetRequiredTarget(UnrealOperationParameters);
                    Plugin plugin = Plugin;
                    PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
                    HostProject = plugin.HostProject;
                    Project hostProject = HostProject;
                    ProjectDescriptor projectDescriptor = hostProject.ProjectDescriptor;

                    if (!projectDescriptor.HasPluginEnabled(plugin.Name))
                    {
                        throw new Exception("Host project must have plugin enabled");
                    }

                    Logger.LogInformation($"Engine version: {engineVersion}");

                    bool bStandardBranch = true;
                    string branchName = VersionControlUtils.GetBranchName(HostProject.ProjectPath);
                    if (!string.IsNullOrEmpty(branchName))
                    {
                        Logger.LogInformation($"Branch: {branchName}");
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

                    archivePrefix = plugin.Name;

                    bool beta = pluginDescriptor.IsBetaVersion;
                    if (beta)
                    {
                        Logger.LogInformation("Plugin is marked as beta version");
                        archivePrefix += "_beta";
                    }

                    string pluginVersionString = pluginDescriptor.VersionName;
                    string fullPluginVersionString = pluginVersionString;

                    if (!string.IsNullOrEmpty(branchName))
                    {
                        if (pluginDescriptor.VersionName.Contains(branchName))
                        {
                            Logger.LogInformation("Branch is contained in plugin version name");
                            fullPluginVersionString = pluginVersionString;
                        }
                        else if (engineVersion.ToString().Contains(branchName))
                        {
                            Logger.LogInformation("Branch is contained in engine version");
                            fullPluginVersionString = pluginVersionString;
                        }
                        else if (!bStandardBranch)
                        {
                            Logger.LogInformation("Branch isn't a version or standard branch");
                            fullPluginVersionString = $"{pluginVersionString}-{branchName.Replace("/", "-")}";
                        }
                        else
                        {
                            Logger.LogInformation("Branch is a version or standard branch");
                            fullPluginVersionString = pluginVersionString;
                        }
                    }

                    archivePrefix += $"_{fullPluginVersionString}";
                    archivePrefix += $"_UE{engineVersion.MajorMinorString}";
                    archivePrefix += "_";

                    Logger.LogInformation($"Archive name prefix is '{archivePrefix}'");

                // Get engine path

                    string enginePath = engine.TargetPath;

                    string enginePluginsMarketplacePath = Path.Combine(enginePath, @"Engine\Plugins\Marketplace");
                    string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, plugin.Name);

                // Delete plugin from engine if installed version exists
                    Policy policy = Policy
                        .Handle<UnauthorizedAccessException>()
                        .RetryForever((ex, retryAttempt, ctx) =>
                        {
                            Logger.LogInformation(ex.ToString());
                            UnrealOperationParameters.RetryHandler?.Invoke(ex);
                        });
                    policy.Execute(() => { FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath); });

                    string workingTempPath = GetOperationTempPath();

                    Directory.CreateDirectory(workingTempPath);

                // Update .uplugin version if required
                    int version = pluginDescriptor.SemVersion.ToInt();
                    Logger.LogInformation($"Version '{pluginDescriptor.VersionName}' -> {version}");
                    bool updated = plugin.UpdateVersionInteger();
                    Logger.LogInformation(updated ? "Updated .uplugin version from name" : ".uplugin already has correct version");

                // Check copyright notice
                    string? copyrightNotice = hostProject.GetCopyrightNotice();

                    if (copyrightNotice == null)
                    {
                        throw new Exception("Project should have a copyright notice");
                    }

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
                            Logger.LogInformation($"Updated copyright notice: {relativePath}");
                        }
                    }
                }

                // Update host project version to match plugin version
                Plugin selectedPlugin = Plugin;
                Project selectedHostProject = HostProject;
                selectedHostProject.SetProjectVersion(selectedPlugin.PluginDescriptor.VersionName, Logger);

                // Create staging copy of plugin with updated descriptor
                using (Logger.BeginSection("Preparing plugin staging copy"))
                {
                    string stagingPluginPath = Path.Combine(GetOperationTempPath(), @"PluginStaging", selectedPlugin.Name);
                    FileUtils.DeleteDirectoryIfExists(stagingPluginPath);
                    FileUtils.CopyDirectory(selectedPlugin.PluginPath, stagingPluginPath);

                    StagingPlugin = new Plugin(stagingPluginPath);
                    Plugin stagingPlugin = StagingPlugin;
                    UpdatePluginDescriptorForArchive(stagingPlugin);
                    Logger.LogInformation($"Updated plugin descriptor for staging: {stagingPlugin.PluginDescriptor.VersionName}");
                }

                AutomationOptions automationOptions = UnrealOperationParameters.GetOptions<AutomationOptions>();

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

                // Run the optional Clang validation while the example project still contains source modules
                // and the built plugin is installed as a project plugin.
                await RunClangCompileCheck();

                await BuildCodeExampleProject();

                // Package code example project with plugin inside project

                await TestCodeExampleProjectWithProjectPlugin(automationOptions);

                // Copy plugin into engine where the marketplace installs it

                await TestCodeExampleProjectWithEnginePlugin(automationOptions);

                // Package blueprint-only example project with plugin installed to engine

                await TestBlueprintExampleProjectWithEnginePlugin(automationOptions);

                await PackageDemoExecutable();

                // Archiving
                await ArchiveArtifacts(archivePrefix);

                Logger.LogInformation($"Finished deploying plugin for {engineVersion.MajorMinorString}");

                return new global::LocalAutomation.Runtime.OperationResult(true);
            }
            finally
            {
                if (!ReferenceEquals(ExampleProject, HostProject))
                {
                    ExampleProject?.Dispose();
                }

                if (!ReferenceEquals(BuiltPlugin, Plugin) && !ReferenceEquals(BuiltPlugin, StagingPlugin))
                {
                    BuiltPlugin?.Dispose();
                }

                if (!ReferenceEquals(StagingPlugin, Plugin))
                {
                    StagingPlugin?.Dispose();
                }
            }
        }

        /// <summary>
        /// Per-engine deployment reuses the same option groups as the outer deployment flow because it reads the shared
        /// deployment settings directly while orchestrating child operations.
        /// </summary>
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(AutomationOptions));
            optionSetTypes.Add(typeof(PluginBuildOptions));
            optionSetTypes.Add(typeof(PluginDeployOptions));
        }

        protected override IEnumerable<LocalAutomation.Runtime.Command> BuildCommands(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            return new List<LocalAutomation.Runtime.Command>();
        }

        private async Task BuildEditor()
        {
            using (Logger.BeginSection("Building host project editor"))
            {
                UnrealOperationParameters buildEditorParams = new()
                {
                    Target = HostProject,
                    EngineOverride = Engine
                };

                if (!(await new BuildEditor().Execute(buildEditorParams, Logger, Token)).Success)
                {
                    throw new Exception("Failed to build host project editor");
                }
            }
        }

        private async Task TestEditor(AutomationOptions automationOptions)
        {
            if (automationOptions.RunTests)
            {
                using (Logger.BeginSection("Launching and testing host project editor"))
                {
                    UnrealOperationParameters launchEditorParams = new()
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
        }

        private async Task TestStandalone(AutomationOptions automationOptions)
        {
            if (automationOptions.RunTests && UnrealOperationParameters.GetOptions<PluginDeployOptions>().TestStandalone)
            {
                using (Logger.BeginSection("Launching and testing standalone"))
                {
                    UnrealOperationParameters launchStandaloneParams = new()
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
        }

        // Package the staged plugin into a distributable output before deployment verification continues.
        private async Task BuildPlugin()
        {
            using (Logger.BeginSection("Building plugin"))
            {
                Plugin plugin = Plugin;
                Plugin stagingPlugin = StagingPlugin;
                Engine engine = Engine;
                string pluginBuildPath = Path.Combine(GetOperationTempPath(), @"PluginBuild", plugin.Name);
                FileUtils.DeleteDirectoryIfExists(pluginBuildPath);

                UnrealOperationParameters buildPluginParams = new()
                {
                    Target = stagingPlugin,
                    EngineOverride = engine,
                    OutputPathOverride = pluginBuildPath
                };
                buildPluginParams.SetOptions(UnrealOperationParameters.GetOptions<PluginBuildOptions>());

                global::LocalAutomation.Runtime.OperationResult buildResult = await new PackagePlugin().Execute(buildPluginParams, Logger, Token);

                if (!buildResult.Success)
                {
                    throw new Exception("Plugin build failed");
                }
                
                BuiltPlugin = new Plugin(pluginBuildPath);

                Logger.LogInformation("Plugin build complete");
            }
        }

        private Task PrepareExampleProject()
        {
            using (Logger.BeginSection("Preparing host project"))
            {
                Project hostProject = HostProject;
                Plugin plugin = Plugin;
                Plugin builtPlugin = BuiltPlugin;
                Engine engine = Engine;
                string uProjectFilename = Path.GetFileName(hostProject.UProjectPath);
                string projectName = Path.GetFileNameWithoutExtension(hostProject.UProjectPath);

                string exampleProjectPath = Path.Combine(GetOperationTempPath(), @"ExampleProject");

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

                ExampleProject = new(exampleProjectPath);
                Project exampleProject = ExampleProject;
                
                // Update project descriptor for archive
                UpdateProjectDescriptorForArchive(exampleProject);
                Logger.LogInformation($"Updated project descriptor for archive: EngineAssociation = {engine.Version}");

                // Copy other plugins

                var sourcePlugins = hostProject.Plugins;
                foreach (Plugin sourcePlugin in sourcePlugins)
                {
                    if (!sourcePlugin.Equals(plugin))
                    {
                        exampleProject.AddPlugin(sourcePlugin);
                    }
                }

                // Copy built plugin to example project
                exampleProject.AddPlugin(builtPlugin);
                
                // Update example project version to match plugin version with engine suffix
                string exampleProjectVersion = ProjectConfig.BuildVersionWithEnginePrefix(plugin.PluginDescriptor.VersionName, engine.Version);
                exampleProject.SetProjectVersion(exampleProjectVersion, Logger);
            }

            return Task.CompletedTask;
        }

        private async Task BuildCodeExampleProject()
        {
            Logger.LogInformation("Building example project with modules");
            // Note: Modules and source are required to build any code plugins that are used for testing
            Project exampleProject = ExampleProject;
            Engine engine = Engine;

            UnrealOperationParameters buildExampleProjectParams = new()
            {
                Target = exampleProject,
                EngineOverride = engine
            };
            global::LocalAutomation.Runtime.OperationResult exampleProjectBuildResult = await new BuildEditor().Execute(buildExampleProjectParams, Logger, Token);
            if (!exampleProjectBuildResult.Success)
            {
                throw new Exception($"Failed to build example project with modules");
            }
        }

        // Reuse the example project's prebuilt editor binaries so the packaging validation passes do not spend time
        // recompiling the editor target before cooking and staging.
        private UnrealOperationParameters CreateExampleProjectPackageParams(string outputPath)
        {
            Project exampleProject = ExampleProject;
            Engine engine = Engine;
            return new UnrealOperationParameters
            {
                Target = exampleProject,
                EngineOverride = engine,
                OutputPathOverride = outputPath,
                AdditionalArguments = "-nocompileeditor"
            };
        }

        // Rebuild the packaged plugin in-place with Clang so validation matches the project-plugin flow Fab uses.
        private async Task RunClangCompileCheck()
        {
            PluginDeployOptions pluginDeployOptions = UnrealOperationParameters.GetOptions<PluginDeployOptions>();
            if (!pluginDeployOptions.RunClangCompileCheck)
            {
                return;
            }

            using (Logger.BeginSection("Running Clang compile check"))
            {
                Project exampleProject = ExampleProject;
                Plugin builtPlugin = BuiltPlugin;
                Engine engine = Engine;
                Plugin exampleProjectPlugin = exampleProject.Plugins.SingleOrDefault(plugin => plugin.Name == builtPlugin.Name);
                if (exampleProjectPlugin == null)
                {
                    throw new Exception("Could not find packaged plugin inside example project for Clang validation");
                }

                UnrealOperationParameters clangBuildParams = new()
                {
                    Target = exampleProjectPlugin,
                    EngineOverride = engine
                };

                // Run the Fab-style Clang validation through the direct plugin build path.
                clangBuildParams.SetOptions(new BuildConfigurationOptions
                {
                    Configuration = BuildConfiguration.Development
                });
                clangBuildParams.SetOptions(new UbtCompilerOptions
                {
                    Compiler = UbtCompiler.Clang
                });

                global::LocalAutomation.Runtime.OperationResult clangBuildResult = await new BuildPlugin().Execute(clangBuildParams, Logger, Token);
                if (!clangBuildResult.Success)
                {
                    throw new Exception("Clang compile check failed");
                }
            }
        }

        private async Task TestCodeExampleProjectWithProjectPlugin(AutomationOptions automationOptions)
        {
            using (Logger.BeginSection("Packaging code example project with plugin inside project"))
            {
                Engine engine = Engine;
                string projectPluginPackagePath = Path.Combine(GetOperationTempPath(), @"ProjectPluginPackage");
                FileUtils.DeleteDirectoryIfExists(projectPluginPackagePath);

                UnrealOperationParameters packageWithPluginParams = CreateExampleProjectPackageParams(projectPluginPackagePath);

                global::LocalAutomation.Runtime.OperationResult buildWithProjectPluginResult = await new PackageProject().Execute(packageWithPluginParams, Logger, Token);

                if (!buildWithProjectPluginResult.Success)
                {
                    throw new Exception("Package project with included plugin failed");
                }
                
                if (automationOptions.RunTests && UnrealOperationParameters.GetOptions<PluginDeployOptions>().TestPackageWithProjectPlugin)
                {
                    using (Logger.BeginSection("Testing code project package with project plugin"))
                    {
                        Package projectPluginPackage = new (Path.Combine(projectPluginPackagePath, engine.GetWindowsPlatformName()));

                        UnrealOperationParameters testProjectPluginPackageParams = new()
                        {
                            Target = projectPluginPackage,
                            EngineOverride = engine
                        };
                        testProjectPluginPackageParams.SetOptions(automationOptions);

                        global::LocalAutomation.Runtime.OperationResult testResult = await new LaunchPackage().Execute(testProjectPluginPackageParams, Logger, Token);

                        if (!testResult.Success)
                        {
                            throw new Exception("Launch and test with project plugin failed");
                        }
                    }
                }
            }
        }

        private async Task TestCodeExampleProjectWithEnginePlugin(AutomationOptions automationOptions)
        {
            Engine engine = Engine;
            Plugin plugin = Plugin;
            Plugin builtPlugin = BuiltPlugin;
            Project exampleProject = ExampleProject;
            using (Logger.BeginSection("Preparing to package example project with installed plugin"))
            {
                string enginePluginsMarketplacePath = Path.Combine(engine.TargetPath, @"Engine\Plugins\Marketplace");
                string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, plugin.Name);

                Logger.LogInformation($"Copying plugin to {enginePluginsMarketplacePluginPath}");

                FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);

                FileUtils.CopyDirectory(builtPlugin.PluginPath, enginePluginsMarketplacePluginPath);
            }

            // Package code example project with plugin installed to engine
            // It's worth doing this to test for build or packaging issues that might only happen using installed plugin

            using (Logger.BeginSection("Packaging code example project with installed plugin"))
            {
                // Remove the plugin in the project because it should only be in the engine
                exampleProject.RemovePlugin(plugin.Name);

                string enginePluginPackagePath = Path.Combine(GetOperationTempPath(), @"EnginePluginPackage");
                FileUtils.DeleteDirectoryIfExists(enginePluginPackagePath);

                UnrealOperationParameters installedPluginPackageParams = CreateExampleProjectPackageParams(enginePluginPackagePath);

                global::LocalAutomation.Runtime.OperationResult installedPluginPackageOperationResult = await new PackageProject().Execute(installedPluginPackageParams, Logger, Token);

                if (!installedPluginPackageOperationResult.Success)
                {
                    throw new Exception("Package project with engine plugin failed");
                }

                // Test the package
                if (automationOptions.RunTests && UnrealOperationParameters.GetOptions<PluginDeployOptions>().TestPackageWithEnginePlugin)
                {
                    using (Logger.BeginSection("Testing code project package with installed plugin"))
                    {
                        Package enginePluginPackage = new Package(Path.Combine(enginePluginPackagePath, engine.GetWindowsPlatformName()));

                        UnrealOperationParameters testEnginePluginPackageParams = new()
                        {
                            Target = enginePluginPackage,
                            EngineOverride = engine
                        };
                        testEnginePluginPackageParams.SetOptions(automationOptions);

                        global::LocalAutomation.Runtime.OperationResult testResult = await new LaunchPackage().Execute(testEnginePluginPackageParams, Logger, Token);

                        if (!testResult.Success)
                        {
                            throw new Exception("Launch and test with installed plugin failed");
                        }
                    }
                }
            }
        }

        private async Task TestBlueprintExampleProjectWithEnginePlugin(AutomationOptions automationOptions)
        {
            Project exampleProject = ExampleProject;
            Engine engine = Engine;
            using (Logger.BeginSection("Packaging blueprint-only example project"))
            {
                exampleProject.ConvertToBlueprintOnly();
                
                PreparePluginsForProject(exampleProject);

                string blueprintOnlyPackagePath = Path.Combine(GetOperationTempPath(), @"BlueprintOnlyPackage");
                FileUtils.DeleteDirectoryIfExists(blueprintOnlyPackagePath);

                UnrealOperationParameters blueprintOnlyPackageParams = CreateExampleProjectPackageParams(blueprintOnlyPackagePath);

                global::LocalAutomation.Runtime.OperationResult blueprintOnlyPackageOperationResult = await new PackageProject().Execute(blueprintOnlyPackageParams, Logger, Token);

                if (!blueprintOnlyPackageOperationResult.Success)
                {
                    throw new Exception("Package blueprint-only project failed");
                }
                
                // Test the package
                if (automationOptions.RunTests && UnrealOperationParameters.GetOptions<PluginDeployOptions>().TestPackageWithEnginePlugin)
                {
                    using (Logger.BeginSection("Testing blueprint project package with installed plugin"))
                    {
                        Package enginePluginPackage = new Package(Path.Combine(blueprintOnlyPackagePath, engine.GetWindowsPlatformName()));

                        UnrealOperationParameters testEnginePluginPackageParams = new()
                        {
                            Target = enginePluginPackage,
                            EngineOverride = engine
                        };
                        testEnginePluginPackageParams.SetOptions(automationOptions);

                        global::LocalAutomation.Runtime.OperationResult testResult = await new LaunchPackage().Execute(testEnginePluginPackageParams, Logger, Token);

                        if (!testResult.Success)
                        {
                            throw new Exception("Launch and test blueprint project with installed plugin failed");
                        }
                    }
                }
            }
        }

        private async Task PackageDemoExecutable()
        {
            Engine engine = Engine;
            Plugin plugin = Plugin;
            Plugin builtPlugin = BuiltPlugin;
            Project exampleProject = ExampleProject;
            // If true, the demo executable will be packaged with the plugin installed to the project
            // This is currently disabled because in 5.3 blueprint-only projects will fail to load plugins that are installed to the project
            bool packageDemoExecutableWithProjectPlugin = false;

            if (packageDemoExecutableWithProjectPlugin)
            {
                // Uninstall plugin from engine because test has completed
                // Now we'll be using the plugin in the project directory instead

                Logger.LogInformation("Uninstall from Engine/Plugins/Marketplace");
                
                engine.UninstallPlugin(plugin.Name);

                // Copy plugin to example project to prepare the demo package
                string exampleProjectPluginPath = Path.Combine(exampleProject.ProjectPath, "Plugins", Path.GetFileName(plugin.PluginPath));
                FileUtils.CopyDirectory(builtPlugin.PluginPath, exampleProjectPluginPath);
            }

            // Package demo executable

            using (Logger.BeginSection("Packaging host project for demo"))
            {
                string demoPackagePath = Path.Combine(GetOperationTempPath(), @"DemoExe");

                FileUtils.DeleteDirectoryIfExists(demoPackagePath);

                PackageProject demoPackageOperation = new();
                UnrealOperationParameters demoPackageParams = (UnrealOperationParameters)demoPackageOperation.CreateParameters(CreateExampleProjectPackageParams(demoPackagePath));

                // Set options for demo exe
                demoPackageParams.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
                demoPackageParams.GetOptions<PackageOptions>().NoDebugInfo = true;

                global::LocalAutomation.Runtime.OperationResult demoExePackageOperationResult = await demoPackageOperation.Execute(demoPackageParams, Logger, Token);

                if (!demoExePackageOperationResult.Success)
                {
                    throw new Exception("Example project build failed");
                }

                DemoPackage = new Package(Path.Combine(demoPackagePath, engine.GetWindowsPlatformName()));

                // Can't test the demo package in shipping
            }
        }

        private void PreparePluginsForProject(Project targetProject)
        {
            Plugin plugin = Plugin;
            var exampleProjectPlugins = targetProject.Plugins;
            
            string[] excludePlugins = UnrealOperationParameters.GetOptions<PluginDeployOptions>().ExcludePlugins.Replace(" ", "").Split(",");
            foreach (Plugin exampleProjectPlugin in exampleProjectPlugins)
            {
                if (exampleProjectPlugin.Name == plugin.Name || !UnrealOperationParameters.GetOptions<PluginDeployOptions>().IncludeOtherPlugins || excludePlugins.Contains(exampleProjectPlugin.Name))
                {
                    // Delete target or excluded plugin from example project
                    FileUtils.DeleteDirectory(exampleProjectPlugin.TargetDirectory);
                }
                else
                {
                    // Other plugins will be included, just delete Intermediate folder
                    string intermediateDirectory = Path.Combine(exampleProjectPlugin.TargetDirectory, "Intermediate");
                    FileUtils.DeleteDirectoryIfExists(intermediateDirectory);
                }
            }
        }

        private Task ArchiveArtifacts(string archivePrefix)
        {
            using (Logger.BeginSection("Archiving"))
            {
                Plugin plugin = Plugin;
                Plugin builtPlugin = BuiltPlugin;
                Project exampleProject = ExampleProject;
                Package demoPackage = DemoPackage;
                Plugin stagingPlugin = StagingPlugin;
                string archivePath = Path.Combine(GetOutputPath(UnrealOperationParameters), "Archives");

                Directory.CreateDirectory(archivePath);

                // Archive plugin build

                string pluginBuildZipPath = Path.Combine(archivePath, archivePrefix + "PluginBuild.zip");
                bool archivePluginBuild = UnrealOperationParameters.GetOptions<PluginDeployOptions>().ArchivePluginBuild;
                if (archivePluginBuild)
                {
                    Logger.LogInformation("Archiving plugin build");
                    FileUtils.DeleteFileIfExists(pluginBuildZipPath);
                    ZipFile.CreateFromDirectory(builtPlugin.PluginPath, pluginBuildZipPath, CompressionLevel.Optimal, true);
                }

                // Archive demo exe

                string demoPackageZipPath = Path.Combine(archivePath, archivePrefix + "DemoPackage.zip");
                bool archiveDemoPackage = UnrealOperationParameters.GetOptions<PluginDeployOptions>().ArchiveDemoPackage;
                if (archiveDemoPackage)
                {
                    Logger.LogInformation("Archiving demo");

                    FileUtils.DeleteFileIfExists(demoPackageZipPath);
                    ZipFile.CreateFromDirectory(demoPackage.TargetPath, demoPackageZipPath);
                }

                // Archive example project

                string exampleProjectZipPath = Path.Combine(archivePath, archivePrefix + "ExampleProject.zip");
                bool archiveExampleProject = UnrealOperationParameters.GetOptions<PluginDeployOptions>().ArchiveExampleProject;

                if (archiveExampleProject)
                {
                    Logger.LogInformation("Archiving example project");

                    // First delete any extra directories
                    string[] allowedExampleProjectSubDirectoryNames = { "Content", "Config", "Plugins" };
                    FileUtils.DeleteOtherSubdirectories(exampleProject.ProjectPath, allowedExampleProjectSubDirectoryNames);
                    
                    PreparePluginsForProject(exampleProject);

                    // Delete debug files recursive
                    FileUtils.DeleteFilesWithExtension(exampleProject.ProjectPath, new[] { ".pdb" }, SearchOption.AllDirectories);

                    FileUtils.DeleteFileIfExists(exampleProjectZipPath);
                    ZipFile.CreateFromDirectory(exampleProject.ProjectPath, exampleProjectZipPath);
                }

                // Archive plugin source for submission

                Logger.LogInformation("Archiving plugin source");

                // Use staging plugin which already has updated descriptor
                string pluginSourcePath = Path.Combine(GetOperationTempPath(), @"PluginSource", plugin.Name);
                FileUtils.DeleteDirectoryIfExists(pluginSourcePath);
                FileUtils.CopyDirectory(stagingPlugin.PluginPath, pluginSourcePath);

                string[] allowedPluginSourceArchiveSubDirectoryNames = { "Source", "Resources", "Content", "Config", "Extras" };
                FileUtils.DeleteOtherSubdirectories(pluginSourcePath, allowedPluginSourceArchiveSubDirectoryNames);

                // Delete top-level files other than uplugin
                FileUtils.DeleteFilesWithoutExtension(pluginSourcePath, _allowedPluginFileExtensions);

                string pluginSourceArchiveZipPath = Path.Combine(archivePath, archivePrefix + "PluginSource.zip");
                FileUtils.DeleteFileIfExists(pluginSourceArchiveZipPath);
                ZipFile.CreateFromDirectory(pluginSourcePath, pluginSourceArchiveZipPath, CompressionLevel.Optimal, true);

                string archiveOutputPath = UnrealOperationParameters.GetOptions<PluginDeployOptions>().ArchivePath;
                if (!string.IsNullOrEmpty(archiveOutputPath))
                {
                    Logger.LogInformation("Copying to archive output path");
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

                Logger.LogInformation("Finished archiving");
            }

            return Task.CompletedTask;
        }
    }
    
    public class DeployPlugin : UnrealOperation<Plugin>
    {
        public override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            string? requirementsError = base.CheckRequirementsSatisfied(operationParameters);
            if (requirementsError != null)
            {
                return requirementsError;
            }

            EngineVersionOptions engineVersionOptions = typedParameters.GetOptions<EngineVersionOptions>();
            if (engineVersionOptions.EnabledVersions.Count == 0)
            {
                return null;
            }

            foreach (EngineVersion engineVersion in engineVersionOptions.EnabledVersions)
            {
                Engine? engine = EngineFinder.GetEngineInstall(engineVersion);
                if (engine == null)
                {
                    return $"Engine {engineVersion.MajorMinorString} not found";
                }

                string? platformRequirementsError = PluginBuildPlatformValidation.CheckRequirementsSatisfied(typedParameters, engine);
                if (platformRequirementsError != null)
                {
                    return $"Engine {engineVersion.MajorMinorString}: {platformRequirementsError}";
                }
            }

            return null;
        }

        protected override async Task<global::LocalAutomation.Runtime.OperationResult> OnExecuted(CancellationToken token)
        {
            Plugin plugin = GetRequiredTarget(UnrealOperationParameters);

            Logger.LogInformation($"Versions: {string.Join(", ", UnrealOperationParameters.GetOptions<EngineVersionOptions>().EnabledVersions.Select(x => x.MajorMinorString)) }");
            foreach (EngineVersion engineVersion in UnrealOperationParameters.GetOptions<EngineVersionOptions>().EnabledVersions)
            {
                Engine? engine = EngineFinder.GetEngineInstall(engineVersion);
                if (engine == null)
                {
                    throw new Exception("Engine not found");
                }
                global::LocalAutomation.Runtime.OperationResult result = await DeployForEngine(engine, token);
                if (!result.Success)
                {
                    // Failure
                    return result;
                }
            }

            return new global::LocalAutomation.Runtime.OperationResult(true);
        }

        private async Task<global::LocalAutomation.Runtime.OperationResult> DeployForEngine(Engine engine, CancellationToken token)
        {
            DeployPluginForEngine deployForEngineOp = new() { Engine = engine };
            return await deployForEngineOp.Execute(UnrealOperationParameters, Logger, token);
        }

        /// <summary>
        /// Plugin deployment exposes engine selection, automation toggles, plugin build settings, and deployment
        /// packaging controls so the user can configure the full archive/test flow up front.
        /// </summary>
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(EngineVersionOptions));
            optionSetTypes.Add(typeof(AutomationOptions));
            optionSetTypes.Add(typeof(PluginBuildOptions));
            optionSetTypes.Add(typeof(PluginDeployOptions));
        }

        protected override IEnumerable<LocalAutomation.Runtime.Command> BuildCommands(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            return new List<LocalAutomation.Runtime.Command>();
        }

        protected override bool FailOnWarning()
        {
            return true;
        }
    }
}
