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
        private string[] _allowedPluginFileExtensions = { ".uplugin" };

        private sealed class DeploymentState
        {
            public DeploymentState(Engine engine, Plugin sourcePlugin, Project hostProject, Plugin? stagingPlugin = null, Plugin? builtPlugin = null, Project? exampleProject = null, Package? demoPackage = null)
            {
                Engine = engine ?? throw new ArgumentNullException(nameof(engine));
                SourcePlugin = sourcePlugin ?? throw new ArgumentNullException(nameof(sourcePlugin));
                HostProject = hostProject ?? throw new ArgumentNullException(nameof(hostProject));
                StagingPlugin = stagingPlugin;
                BuiltPlugin = builtPlugin;
                ExampleProject = exampleProject;
                DemoPackage = demoPackage;
            }

            public Engine Engine { get; }

            public Plugin SourcePlugin { get; }

            public Project HostProject { get; }

            public Plugin? StagingPlugin { get; }

            public Plugin? BuiltPlugin { get; }

            public Project? ExampleProject { get; }

            public Package? DemoPackage { get; }

            public Plugin GetRequiredStagingPlugin()
            {
                return StagingPlugin ?? throw new InvalidOperationException("Staging plugin is not available yet.");
            }

            public Plugin GetRequiredBuiltPlugin()
            {
                return BuiltPlugin ?? throw new InvalidOperationException("Built plugin is not available yet.");
            }

            public Project GetRequiredExampleProject()
            {
                return ExampleProject ?? throw new InvalidOperationException("Example project is not available yet.");
            }

            public Package GetRequiredDemoPackage()
            {
                return DemoPackage ?? throw new InvalidOperationException("Demo package is not available yet.");
            }

            public DeploymentState WithStagingPlugin(Plugin stagingPlugin)
            {
                return new DeploymentState(Engine, SourcePlugin, HostProject, stagingPlugin, BuiltPlugin, ExampleProject, DemoPackage);
            }

            public DeploymentState WithBuiltPlugin(Plugin builtPlugin)
            {
                return new DeploymentState(Engine, SourcePlugin, HostProject, StagingPlugin, builtPlugin, ExampleProject, DemoPackage);
            }

            public DeploymentState WithExampleProject(Project exampleProject)
            {
                return new DeploymentState(Engine, SourcePlugin, HostProject, StagingPlugin, BuiltPlugin, exampleProject, DemoPackage);
            }

            public DeploymentState WithDemoPackage(Package demoPackage)
            {
                return new DeploymentState(Engine, SourcePlugin, HostProject, StagingPlugin, BuiltPlugin, ExampleProject, demoPackage);
            }
        }

        /// <summary>
         /// Gets the isolated per-engine temp root so multiple engine branches can execute without colliding in shared
         /// staging or package folders.
         /// </summary>
        private string GetEngineTempPath(Engine engine)
        {
            return Path.Combine(base.GetOperationTempPath(), $"UE_{engine.Version.MajorMinorString}");
        }

        private void UpdatePluginDescriptorForArchive(DeploymentState state, Plugin plugin)
        {
            Engine engine = state.Engine;
            Plugin sourcePlugin = state.SourcePlugin;
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
        
        private void UpdateProjectDescriptorForArchive(DeploymentState state, Project project)
        {
            Engine engine = state.Engine;
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
      
        /// <summary>
        /// Describes the per-engine deployment subtree beneath the framework-owned root task.
        /// </summary>
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.OperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            AutomationOptions automationOptions = typedParameters.GetOptions<AutomationOptions>();
            PluginDeployOptions deployOptions = typedParameters.GetOptions<PluginDeployOptions>();

            root.Children(steps => steps
                .Task("Prepare")
                    .Describe("Resolve source assets, versioning, and staging prerequisites")
                    .Then(context => PrepareStepAsync(context))
                .Task("Stage Plugin")
                    .Describe("Create the staged plugin copy used for packaging and archiving")
                    .Then(context => StagingStepAsync(context))
                .Task("Build Editor")
                    .Describe("Compile the host project editor before validation runs")
                    .Then(context => BuildEditor(context))
                .Task("Test Editor")
                    .Describe("Run the editor automation validation pass")
                    .When(automationOptions.RunTests, "Run Tests is off.")
                    .Then(context => TestEditor(context, automationOptions))
                .Task("Test Standalone")
                    .Describe("Run the standalone validation pass")
                    .When(automationOptions.RunTests && deployOptions.TestStandalone, automationOptions.RunTests ? "Test Standalone is off." : "Run Tests is off.")
                    .Then(context => TestStandalone(context, automationOptions))
                .Task("Build Plugin")
                    .Describe("Package the staged plugin into a distributable build")
                    .Then(context => BuildPlugin(context))
                .Task("Prepare Example")
                    .Describe("Assemble the example project used for packaging verification")
                    .Then(context => PrepareExampleProject(context))
                .Task("Clang Check")
                    .Describe("Run the optional Clang validation against the packaged plugin")
                    .When(deployOptions.RunClangCompileCheck, "Run Clang Compile Check is off.")
                    .Then(context => RunClangCompileCheck(context))
                .Task("Build Example")
                    .Describe("Compile the example project before packaging verification")
                    .Then(context => BuildCodeExampleProject(context))
                .Task("Package Project Plugin")
                    .Describe("Package the example project with the plugin installed at project level")
                    .Then(context => PackageCodeExampleProjectWithProjectPluginAsync(context, automationOptions))
                .Task("Test Project Plugin")
                    .Describe("Launch and validate the project-plugin package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithProjectPlugin, automationOptions.RunTests ? "Test Package With Project Plugin is off." : "Run Tests is off.")
                    .Then(context => TestCodeExampleProjectPackageWithProjectPluginAsync(context, automationOptions))
                .Task("Install Engine Plugin")
                    .Describe("Install the built plugin into the engine marketplace folder")
                    .Then(context => PrepareEnginePluginInstallAsync(context))
                .Task("Package Engine Plugin")
                    .Describe("Package the example project with the plugin installed to the engine")
                    .Then(context => PackageCodeExampleProjectWithEnginePluginAsync(context, automationOptions))
                .Task("Test Engine Plugin")
                    .Describe("Launch and validate the engine-plugin package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Then(context => TestCodeExampleProjectPackageWithEnginePluginAsync(context, automationOptions))
                .Task("Package Blueprint")
                    .Describe("Package the blueprint-only project with the plugin installed to the engine")
                    .Then(context => PackageBlueprintExampleProjectWithEnginePluginAsync(context, automationOptions))
                .Task("Test Blueprint")
                    .Describe("Launch and validate the blueprint-only package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Then(context => TestBlueprintExampleProjectPackageWithEnginePluginAsync(context, automationOptions))
                .Task("Package Demo")
                    .Describe("Package the demo executable build")
                    .Then(context => PackageDemoExecutable(context))
                .Task("Archive")
                    .Describe("Archive the requested deployment artifacts")
                    .Then(context => ArchiveArtifacts(context)));
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

        private async Task BuildEditor(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building host project editor");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            UnrealOperationParameters buildEditorParams = new()
            {
                Target = state.HostProject,
                EngineOverride = state.Engine
            };

            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new BuildEditor(), buildEditorParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Failed to build host project editor");
        }

        /// <summary>
        /// Executes one nested child operation and translates its outcome into either success, cancellation, or a
        /// domain-specific failure exception for the current deployment step.
        /// </summary>
        private static async Task EnsureChildOperationOutcome(Func<Task<global::LocalAutomation.Runtime.OperationResult>> operation, CancellationToken cancellationToken, string failureMessage)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            global::LocalAutomation.Runtime.OperationResult result = await operation();
            if (result.Outcome == global::LocalAutomation.Runtime.RunOutcome.Succeeded)
            {
                return;
            }

            if (result.Outcome == global::LocalAutomation.Runtime.RunOutcome.Cancelled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new Exception(failureMessage);
        }

        /// <summary>
        /// Runs the scheduler-backed prepare step.
        /// </summary>
        private async Task PrepareStepAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing plugin");
            UnrealOperationParameters unrealOperationParameters = GetUnrealOperationParameters(context);
            Engine engine = unrealOperationParameters.EngineOverride ?? unrealOperationParameters.Engine
                ?? throw new Exception("Engine not specified");
            Plugin plugin = GetRequiredTarget(unrealOperationParameters);
            PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
            Project hostProject = plugin.HostProject;
            DeploymentState state = new(engine, plugin, hostProject);
            ProjectDescriptor projectDescriptor = hostProject.ProjectDescriptor;

            if (!projectDescriptor.HasPluginEnabled(plugin.Name))
            {
                throw new Exception("Host project must have plugin enabled");
            }

            context.Logger.LogInformation($"Engine version: {engine.Version}");
            string archivePrefix = BuildArchivePrefix(state);
            context.Logger.LogInformation($"Archive name prefix is '{archivePrefix}'");

            string enginePluginsMarketplacePath = Path.Combine(engine.TargetPath, @"Engine\Plugins\Marketplace");
            string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, plugin.Name);
            Policy policy = Policy
                .Handle<UnauthorizedAccessException>()
                .RetryForever((ex, retryAttempt, ctx) =>
                {
                    context.Logger.LogInformation(ex.ToString());
                    unrealOperationParameters.RetryHandler?.Invoke(ex);
                });
            policy.Execute(() => { FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath); });

            Directory.CreateDirectory(GetEngineTempPath(engine));
            int version = pluginDescriptor.SemVersion.ToInt();
            context.Logger.LogInformation($"Version '{pluginDescriptor.VersionName}' -> {version}");
            bool updated = plugin.UpdateVersionInteger();
            context.Logger.LogInformation(updated ? "Updated .uplugin version from name" : ".uplugin already has correct version");

            string? copyrightNotice = hostProject.GetCopyrightNotice();
            if (copyrightNotice == null)
            {
                throw new Exception("Project should have a copyright notice");
            }

            string sourcePath = Path.Combine(plugin.TargetDirectory, "Source");
            string expectedComment = $"// {copyrightNotice}";
            foreach (string file in Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                string firstLine;
                using (StreamReader reader = new(file))
                {
                    firstLine = reader.ReadLine();
                }

                if (firstLine != expectedComment)
                {
                    List<string> lines = File.ReadAllLines(file).ToList();
                    if (firstLine.StartsWith("//"))
                    {
                        lines[0] = expectedComment;
                    }
                    else
                    {
                        lines.Insert(0, expectedComment);
                    }

                    File.WriteAllLines(file, lines);
                    string relativePath = Path.GetRelativePath(sourcePath, file);
                    context.Logger.LogInformation($"Updated copyright notice: {relativePath}");
                }
            }

            hostProject.SetProjectVersion(plugin.PluginDescriptor.VersionName, context.Logger);
            context.SetSharedData(state);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Runs the scheduler-backed staging step.
        /// </summary>
        private async Task StagingStepAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing plugin staging copy");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            string stagingPluginPath = Path.Combine(GetEngineTempPath(state.Engine), @"PluginStaging", state.SourcePlugin.Name);
            FileUtils.DeleteDirectoryIfExists(stagingPluginPath);
            FileUtils.CopyDirectory(state.SourcePlugin.PluginPath, stagingPluginPath);
            Plugin stagingPlugin = new(stagingPluginPath);
            UpdatePluginDescriptorForArchive(state, stagingPlugin);
            context.SetSharedData(state.WithStagingPlugin(stagingPlugin));
            context.Logger.LogInformation($"Updated plugin descriptor for staging: {stagingPlugin.PluginDescriptor.VersionName}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Builds the archive filename prefix for the active engine branch.
        /// </summary>
        private string BuildArchivePrefix(DeploymentState state)
        {
            Plugin plugin = state.SourcePlugin;
            PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
            bool standardBranch = true;
            string branchName = VersionControlUtils.GetBranchName(state.HostProject.ProjectPath);
            if (!string.IsNullOrEmpty(branchName))
            {
                string[] standardBranchNames = { "master", "develop", "development" };
                string[] standardBranchPrefixes = { "version/", "release/", "hotfix/" };
                standardBranch = standardBranchNames.Contains(branchName, StringComparer.InvariantCultureIgnoreCase) ||
                                 standardBranchPrefixes.Any(prefix => branchName.StartsWith(prefix, StringComparison.InvariantCultureIgnoreCase));
            }

            string archivePrefix = plugin.Name;
            if (pluginDescriptor.IsBetaVersion)
            {
                archivePrefix += "_beta";
            }

            string pluginVersionString = pluginDescriptor.VersionName;
            string fullPluginVersionString = pluginVersionString;
            if (!string.IsNullOrEmpty(branchName) &&
                !pluginDescriptor.VersionName.Contains(branchName) &&
                !state.Engine.Version.ToString().Contains(branchName) &&
                !standardBranch)
            {
                fullPluginVersionString = $"{pluginVersionString}-{branchName.Replace("/", "-")}";
            }

            archivePrefix += $"_{fullPluginVersionString}";
            archivePrefix += $"_UE{state.Engine.Version.MajorMinorString}";
            archivePrefix += "_";
            return archivePrefix;
        }

        /// <summary>
         /// Runs one optional deployment step or records a skipped node state when the current option values disable it.
         /// </summary>
        private async Task TestEditor(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Launching and testing host project editor");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            UnrealOperationParameters launchEditorParams = new()
            {
                Target = state.HostProject,
                EngineOverride = state.Engine
            };
            launchEditorParams.SetOptions(automationOptions);

            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new LaunchProjectEditor(), launchEditorParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Failed to launch host project");
        }

        private async Task TestStandalone(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Launching and testing standalone");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            UnrealOperationParameters launchStandaloneParams = new()
            {
                Target = state.HostProject,
                EngineOverride = state.Engine
            };
            launchStandaloneParams.SetOptions(automationOptions);

            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new LaunchStandalone(), launchStandaloneParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Failed to launch standalone");
        }

        // Package the staged plugin into a distributable output before deployment verification continues.
        private async Task BuildPlugin(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building plugin");
            UnrealOperationParameters unrealOperationParameters = GetUnrealOperationParameters(context);
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            Plugin stagingPlugin = state.GetRequiredStagingPlugin();
            string pluginBuildPath = Path.Combine(GetEngineTempPath(state.Engine), @"PluginBuild", state.SourcePlugin.Name);
            FileUtils.DeleteDirectoryIfExists(pluginBuildPath);

            UnrealOperationParameters buildPluginParams = new()
            {
                Target = stagingPlugin,
                EngineOverride = state.Engine,
                OutputPathOverride = pluginBuildPath
            };
            buildPluginParams.SetOptions(unrealOperationParameters.GetOptions<PluginBuildOptions>());

            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new PackagePlugin(), buildPluginParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Plugin build failed");
            
            Plugin builtPlugin = new(pluginBuildPath);
            context.SetSharedData(state.WithBuiltPlugin(builtPlugin));

            context.Logger.LogInformation("Plugin build complete");
        }

        private async Task PrepareExampleProject(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing host project");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            Project hostProject = state.HostProject;
            Plugin plugin = state.SourcePlugin;
            Plugin builtPlugin = state.GetRequiredBuiltPlugin();
            Engine engine = state.Engine;
            string uProjectFilename = Path.GetFileName(hostProject.UProjectPath);
            string projectName = Path.GetFileNameWithoutExtension(hostProject.UProjectPath);

            string exampleProjectPath = Path.Combine(GetEngineTempPath(engine), @"ExampleProject");

            FileUtils.DeleteDirectoryIfExists(exampleProjectPath);

            FileUtils.CopySubdirectory(hostProject.ProjectPath, exampleProjectPath, "Content");
            FileUtils.CopySubdirectory(hostProject.ProjectPath, exampleProjectPath, "Config");
            FileUtils.CopySubdirectory(hostProject.ProjectPath, exampleProjectPath, "Source");

            string projectIcon = projectName + ".png";
            if (File.Exists(projectIcon))
            {
                FileUtils.CopyFile(hostProject.ProjectPath, exampleProjectPath, projectIcon);
            }

            JObject uProjectContents = JObject.Parse(File.ReadAllText(hostProject.UProjectPath));
            string exampleProjectBuildUProjectPath = Path.Combine(exampleProjectPath, uProjectFilename);
            File.WriteAllText(exampleProjectBuildUProjectPath, uProjectContents.ToString());

            Project exampleProject = new(exampleProjectPath);

            UpdateProjectDescriptorForArchive(state, exampleProject);
            context.Logger.LogInformation($"Updated project descriptor for archive: EngineAssociation = {engine.Version}");

            var sourcePlugins = hostProject.Plugins;
            foreach (Plugin sourcePlugin in sourcePlugins)
            {
                if (!sourcePlugin.Equals(plugin))
                {
                    exampleProject.AddPlugin(sourcePlugin);
                }
            }

            exampleProject.AddPlugin(builtPlugin);
            string exampleProjectVersion = ProjectConfig.BuildVersionWithEnginePrefix(plugin.PluginDescriptor.VersionName, engine.Version);
            exampleProject.SetProjectVersion(exampleProjectVersion, context.Logger);
            context.SetSharedData(state.WithExampleProject(exampleProject));
            await Task.CompletedTask;
        }

        private async Task BuildCodeExampleProject(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            context.Logger.LogInformation("Building example project with modules");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();

            UnrealOperationParameters buildExampleProjectParams = new()
            {
                Target = state.GetRequiredExampleProject(),
                EngineOverride = state.Engine
            };
            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new BuildEditor(), buildExampleProjectParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Failed to build example project with modules");
        }

        // Reuse the example project's prebuilt editor binaries so the packaging validation passes do not spend time
        // recompiling the editor target before cooking and staging.
        private UnrealOperationParameters CreateExampleProjectPackageParams(DeploymentState state, string outputPath)
        {
            return new UnrealOperationParameters
            {
                Target = state.GetRequiredExampleProject(),
                EngineOverride = state.Engine,
                OutputPathOverride = outputPath,
                AdditionalArguments = "-nocompileeditor"
            };
        }

        // Rebuild the packaged plugin in-place with Clang so validation matches the project-plugin flow Fab uses.
        private async Task RunClangCompileCheck(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Running Clang compile check");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            Project exampleProject = state.GetRequiredExampleProject();
            Plugin builtPlugin = state.GetRequiredBuiltPlugin();
            Plugin exampleProjectPlugin = exampleProject.Plugins.SingleOrDefault(plugin => plugin.Name == builtPlugin.Name);
            if (exampleProjectPlugin == null)
            {
                throw new Exception("Could not find packaged plugin inside example project for Clang validation");
            }

            UnrealOperationParameters clangBuildParams = new()
            {
                Target = exampleProjectPlugin,
                EngineOverride = state.Engine
            };

            clangBuildParams.SetOptions(new BuildConfigurationOptions
            {
                Configuration = BuildConfiguration.Development
            });
            clangBuildParams.SetOptions(new UbtCompilerOptions
            {
                Compiler = UbtCompiler.Clang
            });

            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new BuildPlugin(), clangBuildParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Clang compile check failed");
        }

        /// <summary>
        /// Scheduler wrapper for packaging the code example project with the plugin installed at project level.
        /// </summary>
        private async Task PackageCodeExampleProjectWithProjectPluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging code example project with plugin inside project");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            string projectPluginPackagePath = Path.Combine(GetEngineTempPath(state.Engine), @"ProjectPluginPackage");
            FileUtils.DeleteDirectoryIfExists(projectPluginPackagePath);
            UnrealOperationParameters packageWithPluginParams = CreateExampleProjectPackageParams(state, projectPluginPackagePath);
            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new PackageProject(), packageWithPluginParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Package project with included plugin failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the project-plugin package.
        /// </summary>
        private async Task TestCodeExampleProjectPackageWithProjectPluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing code project package with project plugin");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            string projectPluginPackagePath = Path.Combine(GetEngineTempPath(state.Engine), @"ProjectPluginPackage");
            Package projectPluginPackage = new(Path.Combine(projectPluginPackagePath, state.Engine.GetWindowsPlatformName()));
            UnrealOperationParameters testParams = new()
            {
                Target = projectPluginPackage,
                EngineOverride = state.Engine
            };
            testParams.SetOptions(automationOptions);
            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new LaunchPackage(), testParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Launch and test with project plugin failed");
        }

        /// <summary>
        /// Scheduler wrapper for installing the built plugin into the engine marketplace folder.
        /// </summary>
        private async Task PrepareEnginePluginInstallAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing to package example project with installed plugin");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            string enginePluginsMarketplacePath = Path.Combine(state.Engine.TargetPath, @"Engine\Plugins\Marketplace");
            string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, state.SourcePlugin.Name);
            context.Logger.LogInformation($"Copying plugin to {enginePluginsMarketplacePluginPath}");
            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);
            FileUtils.CopyDirectory(state.GetRequiredBuiltPlugin().PluginPath, enginePluginsMarketplacePluginPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Scheduler wrapper for packaging the code example project with the plugin installed to the engine.
        /// </summary>
        private async Task PackageCodeExampleProjectWithEnginePluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging code example project with installed plugin");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            state.GetRequiredExampleProject().RemovePlugin(state.SourcePlugin.Name);
            string enginePluginPackagePath = Path.Combine(GetEngineTempPath(state.Engine), @"EnginePluginPackage");
            FileUtils.DeleteDirectoryIfExists(enginePluginPackagePath);
            UnrealOperationParameters packageParams = CreateExampleProjectPackageParams(state, enginePluginPackagePath);
            global::LocalAutomation.Runtime.OperationResult result = await RunChildOperationAsync(new PackageProject(), packageParams, context.Logger, context.CancellationToken);
            if (result.Outcome != global::LocalAutomation.Runtime.RunOutcome.Succeeded)
            {
                throw new Exception("Package project with engine plugin failed");
            }
        }

        /// <summary>
         /// Scheduler wrapper for testing the engine-plugin package.
         /// </summary>
        private async Task TestCodeExampleProjectPackageWithEnginePluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing code project package with installed plugin");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            string enginePluginPackagePath = Path.Combine(GetEngineTempPath(state.Engine), @"EnginePluginPackage");
            Package enginePluginPackage = new(Path.Combine(enginePluginPackagePath, state.Engine.GetWindowsPlatformName()));
            UnrealOperationParameters testParams = new()
            {
                Target = enginePluginPackage,
                EngineOverride = state.Engine
            };
            testParams.SetOptions(automationOptions);
            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new LaunchPackage(), testParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Launch and test with installed plugin failed");
        }

        /// <summary>
        /// Scheduler wrapper for packaging the blueprint-only example project.
        /// </summary>
        private async Task PackageBlueprintExampleProjectWithEnginePluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging blueprint-only example project");
            UnrealOperationParameters unrealOperationParameters = GetUnrealOperationParameters(context);
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            state.GetRequiredExampleProject().ConvertToBlueprintOnly();
            PreparePluginsForProject(state, state.GetRequiredExampleProject(), unrealOperationParameters.GetOptions<PluginDeployOptions>());
            string blueprintOnlyPackagePath = Path.Combine(GetEngineTempPath(state.Engine), @"BlueprintOnlyPackage");
            FileUtils.DeleteDirectoryIfExists(blueprintOnlyPackagePath);
            UnrealOperationParameters packageParams = CreateExampleProjectPackageParams(state, blueprintOnlyPackagePath);
            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new PackageProject(), packageParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Package blueprint-only project failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged blueprint-only example project.
        /// </summary>
        private async Task TestBlueprintExampleProjectPackageWithEnginePluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing blueprint project package with installed plugin");
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            string blueprintOnlyPackagePath = Path.Combine(GetEngineTempPath(state.Engine), @"BlueprintOnlyPackage");
            Package package = new(Path.Combine(blueprintOnlyPackagePath, state.Engine.GetWindowsPlatformName()));
            UnrealOperationParameters testParams = new()
            {
                Target = package,
                EngineOverride = state.Engine
            };
            testParams.SetOptions(automationOptions);
            await EnsureChildOperationOutcome(
                () => RunChildOperationAsync(new LaunchPackage(), testParams, context.Logger, context.CancellationToken),
                context.CancellationToken,
                "Launch and test blueprint project with installed plugin failed");
        }

        private async Task PackageDemoExecutable(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            Engine engine = state.Engine;
            Plugin plugin = state.SourcePlugin;
            Plugin builtPlugin = state.GetRequiredBuiltPlugin();
            Project exampleProject = state.GetRequiredExampleProject();
            // If true, the demo executable will be packaged with the plugin installed to the project
            // This is currently disabled because in 5.3 blueprint-only projects will fail to load plugins that are installed to the project
            bool packageDemoExecutableWithProjectPlugin = false;

            if (packageDemoExecutableWithProjectPlugin)
            {
                // Uninstall plugin from engine because test has completed
                // Now we'll be using the plugin in the project directory instead

                context.Logger.LogInformation("Uninstall from Engine/Plugins/Marketplace");
                
                engine.UninstallPlugin(plugin.Name);

                // Copy plugin to example project to prepare the demo package
                string exampleProjectPluginPath = Path.Combine(exampleProject.ProjectPath, "Plugins", Path.GetFileName(plugin.PluginPath));
                FileUtils.CopyDirectory(builtPlugin.PluginPath, exampleProjectPluginPath);
            }

            // Package demo executable

            using IDisposable nodeScope = context.Logger.BeginSection("Packaging host project for demo");
            string demoPackagePath = Path.Combine(GetEngineTempPath(engine), @"DemoExe");

            FileUtils.DeleteDirectoryIfExists(demoPackagePath);

            PackageProject demoPackageOperation = new();
            UnrealOperationParameters demoPackageParams = (UnrealOperationParameters)demoPackageOperation.CreateParameters(CreateExampleProjectPackageParams(state, demoPackagePath));

            demoPackageParams.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
            demoPackageParams.GetOptions<PackageOptions>().NoDebugInfo = true;

            global::LocalAutomation.Runtime.OperationResult demoExePackageOperationResult = await RunChildOperationAsync(demoPackageOperation, demoPackageParams, context.Logger, context.CancellationToken);

            if (!demoExePackageOperationResult.Success)
            {
                throw new Exception("Example project build failed");
            }

            context.SetSharedData(state.WithDemoPackage(new Package(Path.Combine(demoPackagePath, engine.GetWindowsPlatformName()))));
        }

        private void PreparePluginsForProject(DeploymentState state, Project targetProject, PluginDeployOptions deployOptions)
        {
            Plugin plugin = state.SourcePlugin;
            var exampleProjectPlugins = targetProject.Plugins;
            
            string[] excludePlugins = deployOptions.ExcludePlugins.Replace(" ", "").Split(",");
            foreach (Plugin exampleProjectPlugin in exampleProjectPlugins)
            {
                if (exampleProjectPlugin.Name == plugin.Name || !deployOptions.IncludeOtherPlugins || excludePlugins.Contains(exampleProjectPlugin.Name))
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

        private async Task ArchiveArtifacts(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving");
            UnrealOperationParameters unrealOperationParameters = GetUnrealOperationParameters(context);
            PluginDeployOptions deployOptions = unrealOperationParameters.GetOptions<PluginDeployOptions>();
            DeploymentState state = context.GetRequiredSharedData<DeploymentState>();
            Plugin plugin = state.SourcePlugin;
            Plugin builtPlugin = state.GetRequiredBuiltPlugin();
            Project exampleProject = state.GetRequiredExampleProject();
            Package demoPackage = state.GetRequiredDemoPackage();
            Plugin stagingPlugin = state.GetRequiredStagingPlugin();
            string archivePrefix = BuildArchivePrefix(state);
            string archivePath = Path.Combine(GetOutputPath(unrealOperationParameters), "Archives");

            Directory.CreateDirectory(archivePath);

                // Archive plugin build

                string pluginBuildZipPath = Path.Combine(archivePath, archivePrefix + "PluginBuild.zip");
                bool archivePluginBuild = deployOptions.ArchivePluginBuild;
                if (archivePluginBuild)
                {
                    context.Logger.LogInformation("Archiving plugin build");
                    FileUtils.DeleteFileIfExists(pluginBuildZipPath);
                    ZipFile.CreateFromDirectory(builtPlugin.PluginPath, pluginBuildZipPath, CompressionLevel.Optimal, true);
                }

                // Archive demo exe

                string demoPackageZipPath = Path.Combine(archivePath, archivePrefix + "DemoPackage.zip");
                bool archiveDemoPackage = deployOptions.ArchiveDemoPackage;
                if (archiveDemoPackage)
                {
                    context.Logger.LogInformation("Archiving demo");

                    FileUtils.DeleteFileIfExists(demoPackageZipPath);
                    ZipFile.CreateFromDirectory(demoPackage.TargetPath, demoPackageZipPath);
                }

                // Archive example project

                string exampleProjectZipPath = Path.Combine(archivePath, archivePrefix + "ExampleProject.zip");
                bool archiveExampleProject = deployOptions.ArchiveExampleProject;

                if (archiveExampleProject)
                {
                    context.Logger.LogInformation("Archiving example project");

                    // First delete any extra directories
                    string[] allowedExampleProjectSubDirectoryNames = { "Content", "Config", "Plugins" };
                    FileUtils.DeleteOtherSubdirectories(exampleProject.ProjectPath, allowedExampleProjectSubDirectoryNames);
                    
                    PreparePluginsForProject(state, exampleProject, deployOptions);

                    // Delete debug files recursive
                    FileUtils.DeleteFilesWithExtension(exampleProject.ProjectPath, new[] { ".pdb" }, SearchOption.AllDirectories);

                    FileUtils.DeleteFileIfExists(exampleProjectZipPath);
                    ZipFile.CreateFromDirectory(exampleProject.ProjectPath, exampleProjectZipPath);
                }

                // Archive plugin source for submission

                context.Logger.LogInformation("Archiving plugin source");

                // Use staging plugin which already has updated descriptor
                string pluginSourcePath = Path.Combine(GetEngineTempPath(state.Engine), @"PluginSource", plugin.Name);
                FileUtils.DeleteDirectoryIfExists(pluginSourcePath);
                FileUtils.CopyDirectory(stagingPlugin.PluginPath, pluginSourcePath);

                string[] allowedPluginSourceArchiveSubDirectoryNames = { "Source", "Resources", "Content", "Config", "Extras" };
                FileUtils.DeleteOtherSubdirectories(pluginSourcePath, allowedPluginSourceArchiveSubDirectoryNames);

                // Delete top-level files other than uplugin
                FileUtils.DeleteFilesWithoutExtension(pluginSourcePath, _allowedPluginFileExtensions);

                string pluginSourceArchiveZipPath = Path.Combine(archivePath, archivePrefix + "PluginSource.zip");
                FileUtils.DeleteFileIfExists(pluginSourceArchiveZipPath);
                ZipFile.CreateFromDirectory(pluginSourcePath, pluginSourceArchiveZipPath, CompressionLevel.Optimal, true);

                string archiveOutputPath = deployOptions.ArchivePath;
                if (!string.IsNullOrEmpty(archiveOutputPath))
                {
                    context.Logger.LogInformation("Copying to archive output path");
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

                context.Logger.LogInformation("Finished archiving");
                await Task.CompletedTask;
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

        /// <summary>
        /// Describes the deploy-plugin subtree beneath the framework-owned root task.
        /// </summary>
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.OperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            Plugin plugin = GetRequiredTarget(typedParameters);
            IReadOnlyList<EngineVersion> enabledVersions = typedParameters.GetOptions<EngineVersionOptions>().EnabledVersions;
            List<EngineVersion> targetVersions = enabledVersions.Count > 0
                ? enabledVersions.ToList()
                : new List<EngineVersion> { plugin.EngineInstance.Version };

            root.Children(branches =>
            {
                foreach (EngineVersion engineVersion in targetVersions)
                {
                    branches
                        .Task($"UE {engineVersion.MajorMinorString}", "Per-engine deployment branch")
                        .ExpandChildOperation<DeployPluginForEngine>(() => CreateBranchParameters(typedParameters, engineVersion));
                }
            });
        }

        private UnrealOperationParameters CreateBranchParameters(UnrealOperationParameters parentParameters, EngineVersion engineVersion)
        {
            UnrealOperationParameters childParameters = (UnrealOperationParameters)parentParameters.CreateChild();
            Engine engine = EngineFinder.GetEngineInstall(engineVersion)
                ?? throw new Exception($"Engine {engineVersion.MajorMinorString} not found");
            childParameters.EngineOverride = engine;
            return childParameters;
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
