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
    /// <summary>
    /// Centralizes the stable deploy-plugin step keys so preview authoring and runtime execution target the same task
    /// identities without hand-built id strings.
    /// </summary>
    internal static class DeployPluginStepKeys
    {
        public const string Prepare = "prepare";
        public const string Staging = "staging";
        public const string BuildEditor = "build-editor";
        public const string TestEditor = "test-editor";
        public const string TestStandalone = "test-standalone";
        public const string BuildPlugin = "build-plugin";
        public const string PrepareExample = "prepare-example";
        public const string ClangCheck = "clang-check";
        public const string BuildExample = "build-example";
        public const string PackageProjectPlugin = "package-project-plugin";
        public const string TestProjectPlugin = "test-project-plugin";
        public const string PrepareEnginePlugin = "prepare-engine-plugin";
        public const string PackageEnginePlugin = "package-engine-plugin";
        public const string TestEnginePlugin = "test-engine-plugin";
        public const string PackageBlueprint = "package-blueprint";
        public const string TestBlueprint = "test-blueprint";
        public const string PackageDemo = "package-demo";
        public const string Archive = "archive";
    }

    /// <summary>
    /// Creates the stable typed execution identifiers used by both deploy-plugin preview plans and runtime task
    /// execution so UI graphs and session events always agree on task identity.
    /// </summary>
    internal static class DeployPluginExecutionIds
    {
        /// <summary>
        /// Creates the top-level deploy-plugin plan id for the provided plugin target.
        /// </summary>
        public static global::LocalAutomation.Core.ExecutionPlanId CreateDeployPlanId(Plugin plugin)
        {
            return global::LocalAutomation.Core.ExecutionIdentifierFactory.CreatePlanId(nameof(DeployPlugin), plugin.Name);
        }

        /// <summary>
        /// Creates the root group id for the deploy-plugin workspace plan.
        /// </summary>
        public static global::LocalAutomation.Core.ExecutionTaskId CreateDeployRootId(Plugin plugin)
        {
            return global::LocalAutomation.Core.ExecutionIdentifierFactory.CreateTaskId(CreateDeployPlanId(plugin), "root");
        }

        /// <summary>
        /// Creates the per-engine branch id shared by preview and runtime execution.
        /// </summary>
        public static global::LocalAutomation.Core.ExecutionTaskId CreateDeployBranchId(Plugin plugin, EngineVersion engineVersion)
        {
            return global::LocalAutomation.Core.ExecutionIdentifierFactory.CreateTaskId(CreateDeployPlanId(plugin), "branch", engineVersion.MajorMinorString);
        }

        /// <summary>
        /// Creates the per-engine step id shared by preview and runtime execution.
        /// </summary>
        public static global::LocalAutomation.Core.ExecutionTaskId CreateDeployStepId(Plugin plugin, EngineVersion engineVersion, string stepKey)
        {
            return global::LocalAutomation.Core.ExecutionIdentifierFactory.CreateTaskId(CreateDeployPlanId(plugin), "branch", engineVersion.MajorMinorString, "step", stepKey);
        }
    }

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

        /// <summary>
        /// Gets the per-engine branch identifier used by the preview graph and the runtime node-log routing.
        /// </summary>
        private global::LocalAutomation.Core.ExecutionTaskId EngineBranchId => DeployPluginExecutionIds.CreateDeployBranchId(GetRequiredTarget(UnrealOperationParameters), Engine.Version);

        /// <summary>
        /// Gets the isolated per-engine temp root so multiple engine branches can execute without colliding in shared
        /// staging or package folders.
        /// </summary>
        private string EngineTempPath => Path.Combine(base.GetOperationTempPath(), $"UE_{Engine.Version.MajorMinorString}");

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

                AutomationOptions automationOptions = UnrealOperationParameters.GetOptions<AutomationOptions>();
                PluginDeployOptions deployOptions = UnrealOperationParameters.GetOptions<PluginDeployOptions>();
                global::LocalAutomation.Runtime.ExecutionPlanBuilder plan = BuildEnginePlan(automationOptions, deployOptions);
                global::LocalAutomation.Runtime.ExecutionPlanScheduler scheduler = new(Logger, maxParallelism: 1);
                global::LocalAutomation.Runtime.OperationResult result = await scheduler.ExecuteAsync(plan.BuildWorkItems(), token);
                if (result.Outcome != global::LocalAutomation.Core.RunOutcome.Succeeded)
                {
                    return result;
                }

                Logger.LogInformation($"Finished deploying plugin for {engineVersion.MajorMinorString}");

                return global::LocalAutomation.Runtime.OperationResult.Succeeded();
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
        /// Builds the per-engine execution plan used for both preview and scheduled execution.
        /// </summary>
        private global::LocalAutomation.Runtime.ExecutionPlanBuilder BuildEnginePlan(AutomationOptions automationOptions, PluginDeployOptions deployOptions)
        {
            Plugin plugin = GetRequiredTarget(UnrealOperationParameters);
            global::LocalAutomation.Runtime.ExecutionPlanBuilder plan = new($"Deploy {Engine.Version.MajorMinorString}", DeployPluginExecutionIds.CreateDeployPlanId(plugin));
            global::LocalAutomation.Runtime.ExecutionGroupHandle branch = plan.Group(EngineBranchId, $"UE {Engine.Version.MajorMinorString}", "Per-engine deployment branch");
            global::LocalAutomation.Runtime.ExecutionSequenceBuilder sequence = plan.Sequence(branch);
            sequence
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.Prepare), "Prepare")
                    .Describe("Resolve source assets, versioning, and staging prerequisites")
                    .Then(() => PrepareStepAsync())
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.Staging), "Stage Plugin")
                    .Describe("Create the staged plugin copy used for packaging and archiving")
                    .Then(() => StagingStepAsync())
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.BuildEditor), "Build Editor")
                    .Describe("Compile the host project editor before validation runs")
                    .Then(() => BuildEditor())
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.TestEditor), "Test Editor")
                    .Describe("Run the editor automation validation pass")
                    .When(automationOptions.RunTests, "Run Tests is off.")
                    .Then(context => RunOptionalStep(context.TaskId, automationOptions.RunTests, "Run Tests is off.", () => TestEditor(automationOptions)))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.TestStandalone), "Test Standalone")
                    .Describe("Run the standalone validation pass")
                    .When(automationOptions.RunTests && deployOptions.TestStandalone, automationOptions.RunTests ? "Test Standalone is off." : "Run Tests is off.")
                    .Then(context => RunOptionalStep(context.TaskId, automationOptions.RunTests && deployOptions.TestStandalone, automationOptions.RunTests ? "Test Standalone is off." : "Run Tests is off.", () => TestStandalone(automationOptions)))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.BuildPlugin), "Build Plugin")
                    .Describe("Package the staged plugin into a distributable build")
                    .Then(() => BuildPlugin())
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.PrepareExample), "Prepare Example")
                    .Describe("Assemble the example project used for packaging verification")
                    .Then(() => PrepareExampleProject())
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.ClangCheck), "Clang Check")
                    .Describe("Run the optional Clang validation against the packaged plugin")
                    .When(deployOptions.RunClangCompileCheck, "Run Clang Compile Check is off.")
                    .Then(context => RunOptionalStep(context.TaskId, deployOptions.RunClangCompileCheck, "Run Clang Compile Check is off.", () => RunClangCompileCheck()))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.BuildExample), "Build Example")
                    .Describe("Compile the example project before packaging verification")
                    .Then(() => BuildCodeExampleProject())
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.PackageProjectPlugin), "Package Project Plugin")
                    .Describe("Package the example project with the plugin installed at project level")
                    .Then(() => PackageCodeExampleProjectWithProjectPluginAsync(automationOptions))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.TestProjectPlugin), "Test Project Plugin")
                    .Describe("Launch and validate the project-plugin package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithProjectPlugin, automationOptions.RunTests ? "Test Package With Project Plugin is off." : "Run Tests is off.")
                    .Then(context => RunOptionalStep(context.TaskId, automationOptions.RunTests && deployOptions.TestPackageWithProjectPlugin, automationOptions.RunTests ? "Test Package With Project Plugin is off." : "Run Tests is off.", () => TestCodeExampleProjectPackageWithProjectPluginAsync(automationOptions)))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.PrepareEnginePlugin), "Install Engine Plugin")
                    .Describe("Install the built plugin into the engine marketplace folder")
                    .Then(() => PrepareEnginePluginInstallAsync())
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.PackageEnginePlugin), "Package Engine Plugin")
                    .Describe("Package the example project with the plugin installed to the engine")
                    .Then(() => PackageCodeExampleProjectWithEnginePluginAsync(automationOptions))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.TestEnginePlugin), "Test Engine Plugin")
                    .Describe("Launch and validate the engine-plugin package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Then(context => RunOptionalStep(context.TaskId, automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.", () => TestCodeExampleProjectPackageWithEnginePluginAsync(automationOptions)))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.PackageBlueprint), "Package Blueprint")
                    .Describe("Package the blueprint-only project with the plugin installed to the engine")
                    .Then(() => PackageBlueprintExampleProjectWithEnginePluginAsync(automationOptions))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.TestBlueprint), "Test Blueprint")
                    .Describe("Launch and validate the blueprint-only package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Then(context => RunOptionalStep(context.TaskId, automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.", () => TestBlueprintExampleProjectPackageWithEnginePluginAsync(automationOptions)))
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.PackageDemo), "Package Demo")
                    .Describe("Package the demo executable build")
                    .Then(() => PackageDemoExecutable())
                .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, Engine.Version, DeployPluginStepKeys.Archive), "Archive")
                    .Describe("Archive the requested deployment artifacts")
                    .Then(() => ArchiveArtifacts(BuildArchivePrefix()));

            return plan;
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
            await ExecutePlannedStep("build-editor", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("build-editor")).BeginSection("Building host project editor");
                UnrealOperationParameters buildEditorParams = new()
                {
                    Target = HostProject,
                    EngineOverride = Engine
                };

                await EnsureChildOperationOutcome(
                    () => new BuildEditor().Execute(buildEditorParams, GetTaskLogger(GetNodeId("build-editor")), Token),
                    "Failed to build host project editor");
            });
        }

        /// <summary>
        /// Executes one nested child operation and translates its outcome into either success, cancellation, or a
        /// domain-specific failure exception for the current deployment step.
        /// </summary>
        private async Task EnsureChildOperationOutcome(Func<Task<global::LocalAutomation.Runtime.OperationResult>> operation, string failureMessage)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            global::LocalAutomation.Runtime.OperationResult result = await operation();
            if (result.Outcome == global::LocalAutomation.Core.RunOutcome.Succeeded)
            {
                return;
            }

            if (result.Outcome == global::LocalAutomation.Core.RunOutcome.Cancelled)
            {
                throw new OperationCanceledException(Token);
            }

            throw new Exception(failureMessage);
        }

        /// <summary>
        /// Runs the scheduler-backed prepare step.
        /// </summary>
        private Task PrepareStepAsync()
        {
            return ExecutePlannedStep("prepare", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("prepare")).BeginSection("Preparing plugin");
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

                GetTaskLogger(GetNodeId("prepare")).LogInformation($"Engine version: {Engine.Version}");
                string archivePrefix = BuildArchivePrefix();
                GetTaskLogger(GetNodeId("prepare")).LogInformation($"Archive name prefix is '{archivePrefix}'");

                string enginePluginsMarketplacePath = Path.Combine(Engine.TargetPath, @"Engine\Plugins\Marketplace");
                string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, plugin.Name);
                Policy policy = Policy
                    .Handle<UnauthorizedAccessException>()
                    .RetryForever((ex, retryAttempt, ctx) =>
                    {
                        GetTaskLogger(GetNodeId("prepare")).LogInformation(ex.ToString());
                        UnrealOperationParameters.RetryHandler?.Invoke(ex);
                    });
                policy.Execute(() => { FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath); });

                Directory.CreateDirectory(EngineTempPath);
                int version = pluginDescriptor.SemVersion.ToInt();
                GetTaskLogger(GetNodeId("prepare")).LogInformation($"Version '{pluginDescriptor.VersionName}' -> {version}");
                bool updated = plugin.UpdateVersionInteger();
                GetTaskLogger(GetNodeId("prepare")).LogInformation(updated ? "Updated .uplugin version from name" : ".uplugin already has correct version");

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
                        GetTaskLogger(GetNodeId("prepare")).LogInformation($"Updated copyright notice: {relativePath}");
                    }
                }

                hostProject.SetProjectVersion(plugin.PluginDescriptor.VersionName, Logger);
                await Task.CompletedTask;
            });
        }

        /// <summary>
        /// Runs the scheduler-backed staging step.
        /// </summary>
        private Task StagingStepAsync()
        {
            return ExecutePlannedStep("staging", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("staging")).BeginSection("Preparing plugin staging copy");
                string stagingPluginPath = Path.Combine(EngineTempPath, @"PluginStaging", Plugin.Name);
                FileUtils.DeleteDirectoryIfExists(stagingPluginPath);
                FileUtils.CopyDirectory(Plugin.PluginPath, stagingPluginPath);
                StagingPlugin = new Plugin(stagingPluginPath);
                UpdatePluginDescriptorForArchive(StagingPlugin);
                GetTaskLogger(GetNodeId("staging")).LogInformation($"Updated plugin descriptor for staging: {StagingPlugin.PluginDescriptor.VersionName}");
                await Task.CompletedTask;
            });
        }

        /// <summary>
        /// Builds the archive filename prefix for the active engine branch.
        /// </summary>
        private string BuildArchivePrefix()
        {
            Plugin plugin = Plugin;
            PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
            bool standardBranch = true;
            string branchName = VersionControlUtils.GetBranchName(HostProject.ProjectPath);
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
                !Engine.Version.ToString().Contains(branchName) &&
                !standardBranch)
            {
                fullPluginVersionString = $"{pluginVersionString}-{branchName.Replace("/", "-")}";
            }

            archivePrefix += $"_{fullPluginVersionString}";
            archivePrefix += $"_UE{Engine.Version.MajorMinorString}";
            archivePrefix += "_";
            return archivePrefix;
        }

        /// <summary>
         /// Runs one optional deployment step or records a skipped node state when the current option values disable it.
         /// </summary>
        private async Task RunOptionalStep(global::LocalAutomation.Core.ExecutionTaskId taskId, bool enabled, string skippedReason, Func<Task> executeAsync)
        {
            if (!enabled)
            {
                SetTaskStatus(taskId, LocalAutomation.Core.ExecutionTaskStatus.Skipped, skippedReason);
                return;
            }

            await executeAsync();
        }

        private async Task TestEditor(AutomationOptions automationOptions)
        {
            if (automationOptions.RunTests)
            {
                await ExecutePlannedStep("test-editor", async () =>
                {
                    using IDisposable nodeScope = GetTaskLogger(GetNodeId("test-editor")).BeginSection("Launching and testing host project editor");
                    UnrealOperationParameters launchEditorParams = new()
                    {
                        Target = HostProject,
                        EngineOverride = Engine
                    };
                    launchEditorParams.SetOptions(automationOptions);

                    await EnsureChildOperationOutcome(
                        () => new LaunchProjectEditor().Execute(launchEditorParams, GetTaskLogger(GetNodeId("test-editor")), Token),
                        "Failed to launch host project");
                });
            }
        }

        private async Task TestStandalone(AutomationOptions automationOptions)
        {
            if (automationOptions.RunTests && UnrealOperationParameters.GetOptions<PluginDeployOptions>().TestStandalone)
            {
                await ExecutePlannedStep("test-standalone", async () =>
                {
                    using IDisposable nodeScope = GetTaskLogger(GetNodeId("test-standalone")).BeginSection("Launching and testing standalone");
                    UnrealOperationParameters launchStandaloneParams = new()
                    {
                        Target = HostProject,
                        EngineOverride = Engine
                    };
                    launchStandaloneParams.SetOptions(automationOptions);

                    await EnsureChildOperationOutcome(
                        () => new LaunchStandalone().Execute(launchStandaloneParams, GetTaskLogger(GetNodeId("test-standalone")), Token),
                        "Failed to launch standalone");
                });
            }
        }

        // Package the staged plugin into a distributable output before deployment verification continues.
        private async Task BuildPlugin()
        {
            await ExecutePlannedStep("build-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("build-plugin")).BeginSection("Building plugin");
                Plugin plugin = Plugin;
                Plugin stagingPlugin = StagingPlugin;
                Engine engine = Engine;
                string pluginBuildPath = Path.Combine(EngineTempPath, @"PluginBuild", plugin.Name);
                FileUtils.DeleteDirectoryIfExists(pluginBuildPath);

                UnrealOperationParameters buildPluginParams = new()
                {
                    Target = stagingPlugin,
                    EngineOverride = engine,
                    OutputPathOverride = pluginBuildPath
                };
                buildPluginParams.SetOptions(UnrealOperationParameters.GetOptions<PluginBuildOptions>());

                await EnsureChildOperationOutcome(
                    () => new PackagePlugin().Execute(buildPluginParams, GetTaskLogger(GetNodeId("build-plugin")), Token),
                    "Plugin build failed");
                
                BuiltPlugin = new Plugin(pluginBuildPath);

                GetTaskLogger(GetNodeId("build-plugin")).LogInformation("Plugin build complete");
            });
        }

        private Task PrepareExampleProject()
        {
            return ExecutePlannedStep("prepare-example", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("prepare-example")).BeginSection("Preparing host project");
                Project hostProject = HostProject;
                Plugin plugin = Plugin;
                Plugin builtPlugin = BuiltPlugin;
                Engine engine = Engine;
                string uProjectFilename = Path.GetFileName(hostProject.UProjectPath);
                string projectName = Path.GetFileNameWithoutExtension(hostProject.UProjectPath);

                string exampleProjectPath = Path.Combine(EngineTempPath, @"ExampleProject");

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
                GetTaskLogger(GetNodeId("prepare-example")).LogInformation($"Updated project descriptor for archive: EngineAssociation = {engine.Version}");

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
                await Task.CompletedTask;
            });
        }

        private async Task BuildCodeExampleProject()
        {
            await ExecutePlannedStep("build-example", async () =>
            {
                GetTaskLogger(GetNodeId("build-example")).LogInformation("Building example project with modules");
                Project exampleProject = ExampleProject;
                Engine engine = Engine;

                UnrealOperationParameters buildExampleProjectParams = new()
                {
                    Target = exampleProject,
                    EngineOverride = engine
                };
                await EnsureChildOperationOutcome(
                    () => new BuildEditor().Execute(buildExampleProjectParams, GetTaskLogger(GetNodeId("build-example")), Token),
                    "Failed to build example project with modules");
            });
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

            await ExecutePlannedStep("clang-check", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("clang-check")).BeginSection("Running Clang compile check");
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

                await EnsureChildOperationOutcome(
                    () => new BuildPlugin().Execute(clangBuildParams, GetTaskLogger(GetNodeId("clang-check")), Token),
                    "Clang compile check failed");
            });
        }

        private async Task TestCodeExampleProjectWithProjectPlugin(AutomationOptions automationOptions)
        {
            await ExecutePlannedStep("package-project-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("package-project-plugin")).BeginSection("Packaging code example project with plugin inside project");
                Engine engine = Engine;
                string projectPluginPackagePath = Path.Combine(EngineTempPath, @"ProjectPluginPackage");
                FileUtils.DeleteDirectoryIfExists(projectPluginPackagePath);

                UnrealOperationParameters packageWithPluginParams = CreateExampleProjectPackageParams(projectPluginPackagePath);

                await EnsureChildOperationOutcome(
                    () => new PackageProject().Execute(packageWithPluginParams, GetTaskLogger(GetNodeId("package-project-plugin")), Token),
                    "Package project with included plugin failed");
                
                if (automationOptions.RunTests && UnrealOperationParameters.GetOptions<PluginDeployOptions>().TestPackageWithProjectPlugin)
                {
                    await ExecutePlannedStep("test-project-plugin", async () =>
                    {
                        using IDisposable testNodeScope = GetTaskLogger(GetNodeId("test-project-plugin")).BeginSection("Testing code project package with project plugin");
                        Package projectPluginPackage = new (Path.Combine(projectPluginPackagePath, engine.GetWindowsPlatformName()));

                        UnrealOperationParameters testProjectPluginPackageParams = new()
                        {
                            Target = projectPluginPackage,
                            EngineOverride = engine
                        };
                        testProjectPluginPackageParams.SetOptions(automationOptions);

                        await EnsureChildOperationOutcome(
                            () => new LaunchPackage().Execute(testProjectPluginPackageParams, GetTaskLogger(GetNodeId("test-project-plugin")), Token),
                            "Launch and test with project plugin failed");
                    });
                }
            });
        }

        /// <summary>
        /// Scheduler wrapper for packaging the code example project with the plugin installed at project level.
        /// </summary>
        private Task PackageCodeExampleProjectWithProjectPluginAsync(AutomationOptions automationOptions)
        {
            return ExecutePlannedStep("package-project-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("package-project-plugin")).BeginSection("Packaging code example project with plugin inside project");
                string projectPluginPackagePath = Path.Combine(EngineTempPath, @"ProjectPluginPackage");
                FileUtils.DeleteDirectoryIfExists(projectPluginPackagePath);
                UnrealOperationParameters packageWithPluginParams = CreateExampleProjectPackageParams(projectPluginPackagePath);
                await EnsureChildOperationOutcome(
                    () => new PackageProject().Execute(packageWithPluginParams, GetTaskLogger(GetNodeId("package-project-plugin")), Token),
                    "Package project with included plugin failed");
            });
        }

        /// <summary>
        /// Scheduler wrapper for testing the project-plugin package.
        /// </summary>
        private Task TestCodeExampleProjectPackageWithProjectPluginAsync(AutomationOptions automationOptions)
        {
            return ExecutePlannedStep("test-project-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("test-project-plugin")).BeginSection("Testing code project package with project plugin");
                string projectPluginPackagePath = Path.Combine(EngineTempPath, @"ProjectPluginPackage");
                Package projectPluginPackage = new(Path.Combine(projectPluginPackagePath, Engine.GetWindowsPlatformName()));
                UnrealOperationParameters testParams = new()
                {
                    Target = projectPluginPackage,
                    EngineOverride = Engine
                };
                testParams.SetOptions(automationOptions);
                await EnsureChildOperationOutcome(
                    () => new LaunchPackage().Execute(testParams, GetTaskLogger(GetNodeId("test-project-plugin")), Token),
                    "Launch and test with project plugin failed");
            });
        }

        private async Task TestCodeExampleProjectWithEnginePlugin(AutomationOptions automationOptions)
        {
            Engine engine = Engine;
            Plugin plugin = Plugin;
            Plugin builtPlugin = BuiltPlugin;
            Project exampleProject = ExampleProject;
            await ExecutePlannedStep("prepare-engine-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("prepare-engine-plugin")).BeginSection("Preparing to package example project with installed plugin");
                string enginePluginsMarketplacePath = Path.Combine(engine.TargetPath, @"Engine\Plugins\Marketplace");
                string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, plugin.Name);

                GetTaskLogger(GetNodeId("prepare-engine-plugin")).LogInformation($"Copying plugin to {enginePluginsMarketplacePluginPath}");

                FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);

                FileUtils.CopyDirectory(builtPlugin.PluginPath, enginePluginsMarketplacePluginPath);
                await Task.CompletedTask;
            });

            // Package code example project with plugin installed to engine
            // It's worth doing this to test for build or packaging issues that might only happen using installed plugin

            await ExecutePlannedStep("package-engine-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("package-engine-plugin")).BeginSection("Packaging code example project with installed plugin");
                // Remove the plugin in the project because it should only be in the engine
                exampleProject.RemovePlugin(plugin.Name);

                string enginePluginPackagePath = Path.Combine(EngineTempPath, @"EnginePluginPackage");
                FileUtils.DeleteDirectoryIfExists(enginePluginPackagePath);

                UnrealOperationParameters installedPluginPackageParams = CreateExampleProjectPackageParams(enginePluginPackagePath);

                await EnsureChildOperationOutcome(
                    () => new PackageProject().Execute(installedPluginPackageParams, GetTaskLogger(GetNodeId("package-engine-plugin")), Token),
                    "Package project with engine plugin failed");

                // Test the package
                if (automationOptions.RunTests && UnrealOperationParameters.GetOptions<PluginDeployOptions>().TestPackageWithEnginePlugin)
                {
                    await ExecutePlannedStep("test-engine-plugin", async () =>
                    {
                        using IDisposable testNodeScope = GetTaskLogger(GetNodeId("test-engine-plugin")).BeginSection("Testing code project package with installed plugin");
                        Package enginePluginPackage = new Package(Path.Combine(enginePluginPackagePath, engine.GetWindowsPlatformName()));

                        UnrealOperationParameters testEnginePluginPackageParams = new()
                        {
                            Target = enginePluginPackage,
                            EngineOverride = engine
                        };
                        testEnginePluginPackageParams.SetOptions(automationOptions);

                        await EnsureChildOperationOutcome(
                            () => new LaunchPackage().Execute(testEnginePluginPackageParams, GetTaskLogger(GetNodeId("test-engine-plugin")), Token),
                            "Launch and test with installed plugin failed");
                    });
                }
            });
        }

        /// <summary>
        /// Scheduler wrapper for installing the built plugin into the engine marketplace folder.
        /// </summary>
        private Task PrepareEnginePluginInstallAsync()
        {
            return ExecutePlannedStep("prepare-engine-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("prepare-engine-plugin")).BeginSection("Preparing to package example project with installed plugin");
                string enginePluginsMarketplacePath = Path.Combine(Engine.TargetPath, @"Engine\Plugins\Marketplace");
                string enginePluginsMarketplacePluginPath = Path.Combine(enginePluginsMarketplacePath, Plugin.Name);
                GetTaskLogger(GetNodeId("prepare-engine-plugin")).LogInformation($"Copying plugin to {enginePluginsMarketplacePluginPath}");
                FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);
                FileUtils.CopyDirectory(BuiltPlugin.PluginPath, enginePluginsMarketplacePluginPath);
                await Task.CompletedTask;
            });
        }

        /// <summary>
        /// Scheduler wrapper for packaging the code example project with the plugin installed to the engine.
        /// </summary>
        private Task PackageCodeExampleProjectWithEnginePluginAsync(AutomationOptions automationOptions)
        {
            return ExecutePlannedStep("package-engine-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("package-engine-plugin")).BeginSection("Packaging code example project with installed plugin");
                ExampleProject.RemovePlugin(Plugin.Name);
                string enginePluginPackagePath = Path.Combine(EngineTempPath, @"EnginePluginPackage");
                FileUtils.DeleteDirectoryIfExists(enginePluginPackagePath);
                UnrealOperationParameters packageParams = CreateExampleProjectPackageParams(enginePluginPackagePath);
                global::LocalAutomation.Runtime.OperationResult result = await new PackageProject().Execute(packageParams, GetTaskLogger(GetNodeId("package-engine-plugin")), Token);
                if (result.Outcome != global::LocalAutomation.Core.RunOutcome.Succeeded)
                {
                    throw new Exception("Package project with engine plugin failed");
                }
            });
        }

        /// <summary>
        /// Scheduler wrapper for testing the engine-plugin package.
        /// </summary>
        private Task TestCodeExampleProjectPackageWithEnginePluginAsync(AutomationOptions automationOptions)
        {
            return ExecutePlannedStep("test-engine-plugin", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("test-engine-plugin")).BeginSection("Testing code project package with installed plugin");
                string enginePluginPackagePath = Path.Combine(EngineTempPath, @"EnginePluginPackage");
                Package enginePluginPackage = new(Path.Combine(enginePluginPackagePath, Engine.GetWindowsPlatformName()));
                UnrealOperationParameters testParams = new()
                {
                    Target = enginePluginPackage,
                    EngineOverride = Engine
                };
                testParams.SetOptions(automationOptions);
                await EnsureChildOperationOutcome(
                    () => new LaunchPackage().Execute(testParams, GetTaskLogger(GetNodeId("test-engine-plugin")), Token),
                    "Launch and test with installed plugin failed");
            });
        }

        private async Task TestBlueprintExampleProjectWithEnginePlugin(AutomationOptions automationOptions)
        {
            Project exampleProject = ExampleProject;
            Engine engine = Engine;
            await ExecutePlannedStep("package-blueprint", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("package-blueprint")).BeginSection("Packaging blueprint-only example project");
                exampleProject.ConvertToBlueprintOnly();
                
                PreparePluginsForProject(exampleProject);

                string blueprintOnlyPackagePath = Path.Combine(EngineTempPath, @"BlueprintOnlyPackage");
                FileUtils.DeleteDirectoryIfExists(blueprintOnlyPackagePath);

                UnrealOperationParameters blueprintOnlyPackageParams = CreateExampleProjectPackageParams(blueprintOnlyPackagePath);

                await EnsureChildOperationOutcome(
                    () => new PackageProject().Execute(blueprintOnlyPackageParams, GetTaskLogger(GetNodeId("package-blueprint")), Token),
                    "Package blueprint-only project failed");
                
                // Test the package
                if (automationOptions.RunTests && UnrealOperationParameters.GetOptions<PluginDeployOptions>().TestPackageWithEnginePlugin)
                {
                    await ExecutePlannedStep("test-blueprint", async () =>
                    {
                        using IDisposable testNodeScope = GetTaskLogger(GetNodeId("test-blueprint")).BeginSection("Testing blueprint project package with installed plugin");
                        Package enginePluginPackage = new Package(Path.Combine(blueprintOnlyPackagePath, engine.GetWindowsPlatformName()));

                        UnrealOperationParameters testEnginePluginPackageParams = new()
                        {
                            Target = enginePluginPackage,
                            EngineOverride = engine
                        };
                        testEnginePluginPackageParams.SetOptions(automationOptions);

                        await EnsureChildOperationOutcome(
                            () => new LaunchPackage().Execute(testEnginePluginPackageParams, GetTaskLogger(GetNodeId("test-blueprint")), Token),
                            "Launch and test blueprint project with installed plugin failed");
                    });
                }
            });
        }

        /// <summary>
        /// Scheduler wrapper for packaging the blueprint-only example project.
        /// </summary>
        private Task PackageBlueprintExampleProjectWithEnginePluginAsync(AutomationOptions automationOptions)
        {
            return ExecutePlannedStep("package-blueprint", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("package-blueprint")).BeginSection("Packaging blueprint-only example project");
                ExampleProject.ConvertToBlueprintOnly();
                PreparePluginsForProject(ExampleProject);
                string blueprintOnlyPackagePath = Path.Combine(EngineTempPath, @"BlueprintOnlyPackage");
                FileUtils.DeleteDirectoryIfExists(blueprintOnlyPackagePath);
                UnrealOperationParameters packageParams = CreateExampleProjectPackageParams(blueprintOnlyPackagePath);
                await EnsureChildOperationOutcome(
                    () => new PackageProject().Execute(packageParams, GetTaskLogger(GetNodeId("package-blueprint")), Token),
                    "Package blueprint-only project failed");
            });
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged blueprint-only example project.
        /// </summary>
        private Task TestBlueprintExampleProjectPackageWithEnginePluginAsync(AutomationOptions automationOptions)
        {
            return ExecutePlannedStep("test-blueprint", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("test-blueprint")).BeginSection("Testing blueprint project package with installed plugin");
                string blueprintOnlyPackagePath = Path.Combine(EngineTempPath, @"BlueprintOnlyPackage");
                Package package = new(Path.Combine(blueprintOnlyPackagePath, Engine.GetWindowsPlatformName()));
                UnrealOperationParameters testParams = new()
                {
                    Target = package,
                    EngineOverride = Engine
                };
                testParams.SetOptions(automationOptions);
                await EnsureChildOperationOutcome(
                    () => new LaunchPackage().Execute(testParams, GetTaskLogger(GetNodeId("test-blueprint")), Token),
                    "Launch and test blueprint project with installed plugin failed");
            });
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

            await ExecutePlannedStep("package-demo", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("package-demo")).BeginSection("Packaging host project for demo");
                string demoPackagePath = Path.Combine(EngineTempPath, @"DemoExe");

                FileUtils.DeleteDirectoryIfExists(demoPackagePath);

                PackageProject demoPackageOperation = new();
                UnrealOperationParameters demoPackageParams = (UnrealOperationParameters)demoPackageOperation.CreateParameters(CreateExampleProjectPackageParams(demoPackagePath));

                // Set options for demo exe
                demoPackageParams.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
                demoPackageParams.GetOptions<PackageOptions>().NoDebugInfo = true;

                global::LocalAutomation.Runtime.OperationResult demoExePackageOperationResult = await demoPackageOperation.Execute(demoPackageParams, GetTaskLogger(GetNodeId("package-demo")), Token);

                if (!demoExePackageOperationResult.Success)
                {
                    throw new Exception("Example project build failed");
                }

                DemoPackage = new Package(Path.Combine(demoPackagePath, engine.GetWindowsPlatformName()));

                // Can't test the demo package in shipping
            });
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

        /// <summary>
        /// Runs one named deployment step with explicit node-state transitions so the session graph can reflect running,
        /// completed, and failed states without inferring them from human-readable output.
        /// </summary>
        private async Task ExecutePlannedStep(string stepKey, Func<Task> step)
        {
            if (step == null)
            {
                throw new ArgumentNullException(nameof(step));
            }

            global::LocalAutomation.Core.ExecutionTaskId nodeId = GetNodeId(stepKey);
            Token.ThrowIfCancellationRequested();
            SetTaskStatus(nodeId, LocalAutomation.Core.ExecutionTaskStatus.Running);
            try
            {
                await step();
                Token.ThrowIfCancellationRequested();
                SetTaskStatus(nodeId, LocalAutomation.Core.ExecutionTaskStatus.Completed);
            }
            catch (OperationCanceledException)
            {
                SetCancelled();
                SetTaskStatus(nodeId, LocalAutomation.Core.ExecutionTaskStatus.Cancelled, "Cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                SetTaskStatus(nodeId, LocalAutomation.Core.ExecutionTaskStatus.Failed, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Builds the stable node id for one step inside the current per-engine deployment branch.
        /// </summary>
        private global::LocalAutomation.Core.ExecutionTaskId GetNodeId(string stepKey)
        {
            return DeployPluginExecutionIds.CreateDeployStepId(GetRequiredTarget(UnrealOperationParameters), Engine.Version, stepKey);
        }

        private Task ArchiveArtifacts(string archivePrefix)
        {
            return ExecutePlannedStep("archive", async () =>
            {
                using IDisposable nodeScope = GetTaskLogger(GetNodeId("archive")).BeginSection("Archiving");
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
                    GetTaskLogger(GetNodeId("archive")).LogInformation("Archiving plugin build");
                    FileUtils.DeleteFileIfExists(pluginBuildZipPath);
                    ZipFile.CreateFromDirectory(builtPlugin.PluginPath, pluginBuildZipPath, CompressionLevel.Optimal, true);
                }

                // Archive demo exe

                string demoPackageZipPath = Path.Combine(archivePath, archivePrefix + "DemoPackage.zip");
                bool archiveDemoPackage = UnrealOperationParameters.GetOptions<PluginDeployOptions>().ArchiveDemoPackage;
                if (archiveDemoPackage)
                {
                    GetTaskLogger(GetNodeId("archive")).LogInformation("Archiving demo");

                    FileUtils.DeleteFileIfExists(demoPackageZipPath);
                    ZipFile.CreateFromDirectory(demoPackage.TargetPath, demoPackageZipPath);
                }

                // Archive example project

                string exampleProjectZipPath = Path.Combine(archivePath, archivePrefix + "ExampleProject.zip");
                bool archiveExampleProject = UnrealOperationParameters.GetOptions<PluginDeployOptions>().ArchiveExampleProject;

                if (archiveExampleProject)
                {
                    GetTaskLogger(GetNodeId("archive")).LogInformation("Archiving example project");

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

                GetTaskLogger(GetNodeId("archive")).LogInformation("Archiving plugin source");

                // Use staging plugin which already has updated descriptor
                string pluginSourcePath = Path.Combine(EngineTempPath, @"PluginSource", plugin.Name);
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
                    GetTaskLogger(GetNodeId("archive")).LogInformation("Copying to archive output path");
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

                GetTaskLogger(GetNodeId("archive")).LogInformation("Finished archiving");
                await Task.CompletedTask;
            });
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
            IReadOnlyList<EngineVersion> selectedVersions = UnrealOperationParameters.GetOptions<EngineVersionOptions>().EnabledVersions;
            List<EngineVersion> targetVersions = selectedVersions.Count > 0
                ? selectedVersions.ToList()
                : new List<EngineVersion> { plugin.EngineInstance.Version };
            Logger.LogInformation($"Versions: {string.Join(", ", targetVersions.Select(x => x.MajorMinorString)) }");

            List<global::LocalAutomation.Runtime.ExecutionWorkItem> workItems = new();
            foreach (EngineVersion engineVersion in targetVersions)
            {
                global::LocalAutomation.Core.ExecutionTaskId branchId = DeployPluginExecutionIds.CreateDeployBranchId(plugin, engineVersion);
                EngineVersion scheduledVersion = engineVersion;
                workItems.Add(new global::LocalAutomation.Runtime.ExecutionWorkItem(
                    taskId: branchId,
                    title: $"UE {engineVersion.MajorMinorString}",
                    executeAsync: async context =>
                    {
                        SetTaskStatus(branchId, LocalAutomation.Core.ExecutionTaskStatus.Running);
                        try
                        {
                            Engine? engine = EngineFinder.GetEngineInstall(scheduledVersion);
                            if (engine == null)
                            {
                                throw new Exception($"Engine {scheduledVersion.MajorMinorString} not found");
                            }

                            global::LocalAutomation.Runtime.OperationResult result = await DeployForEngine(engine, context.Logger, context.CancellationToken);
                            LocalAutomation.Core.ExecutionTaskStatus branchStatus = result.Outcome == global::LocalAutomation.Core.RunOutcome.Succeeded
                                ? LocalAutomation.Core.ExecutionTaskStatus.Completed
                                : result.Outcome == global::LocalAutomation.Core.RunOutcome.Cancelled
                                    ? LocalAutomation.Core.ExecutionTaskStatus.Cancelled
                                    : LocalAutomation.Core.ExecutionTaskStatus.Failed;
                            SetTaskStatus(branchId, branchStatus, result.Outcome == global::LocalAutomation.Core.RunOutcome.Failed ? "Engine deployment failed." : result.Outcome == global::LocalAutomation.Core.RunOutcome.Cancelled ? "Cancelled." : null);
                            return result;
                        }
                        catch (OperationCanceledException)
                        {
                            SetCancelled();
                            SetTaskStatus(branchId, LocalAutomation.Core.ExecutionTaskStatus.Cancelled, "Cancelled.");
                            throw;
                        }
                        catch (Exception ex)
                        {
                            SetTaskStatus(branchId, LocalAutomation.Core.ExecutionTaskStatus.Failed, ex.Message);
                            throw;
                        }
                    }));
            }

            global::LocalAutomation.Runtime.ExecutionPlanScheduler scheduler = new(Logger, maxParallelism: 1);
            return await scheduler.ExecuteAsync(workItems, token);
        }

        /// <summary>
        /// Builds a preview graph for plugin deployment that shows one per-engine branch and the major deployment
        /// stages, including optional test and archive nodes that may be disabled by the current option values.
        /// </summary>
        public override LocalAutomation.Core.ExecutionPlan? BuildExecutionPlan(global::LocalAutomation.Runtime.OperationParameters operationParameters)
        {
            UnrealOperationParameters typedParameters = (UnrealOperationParameters)operationParameters;
            Plugin plugin = GetRequiredTarget(typedParameters);
            IReadOnlyList<EngineVersion> enabledVersions = typedParameters.GetOptions<EngineVersionOptions>().EnabledVersions;
            List<EngineVersion> targetVersions = enabledVersions.Count > 0
                ? enabledVersions.ToList()
                : new List<EngineVersion> { plugin.EngineInstance.Version };
            AutomationOptions automationOptions = typedParameters.GetOptions<AutomationOptions>();
            PluginDeployOptions deployOptions = typedParameters.GetOptions<PluginDeployOptions>();
            global::LocalAutomation.Core.ExecutionPlanId planId = DeployPluginExecutionIds.CreateDeployPlanId(plugin);
            global::LocalAutomation.Runtime.ExecutionPlanBuilder plan = new(OperationName, planId);
            global::LocalAutomation.Runtime.ExecutionGroupHandle root = plan.Group(DeployPluginExecutionIds.CreateDeployRootId(plugin), OperationName, plugin.DisplayName);

            foreach (EngineVersion engineVersion in targetVersions)
            {
                global::LocalAutomation.Runtime.ExecutionGroupHandle branch = plan.Group(DeployPluginExecutionIds.CreateDeployBranchId(plugin, engineVersion), $"UE {engineVersion.MajorMinorString}", "Per-engine deployment branch", root);
                plan.Sequence(branch)
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.Prepare), "Preparing plugin", "Resolve source assets, versioning, and staging prerequisites")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.Staging), "Preparing plugin staging copy", "Create the staged plugin copy used for packaging and archiving")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.BuildEditor), "Building host project editor", "Compile the host project editor before validation runs")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.TestEditor), "Launching and testing host project editor", "Run the editor automation validation pass").When(automationOptions.RunTests, "Disabled because Run Tests is off")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.TestStandalone), "Launching and testing standalone", "Run the standalone validation pass").When(automationOptions.RunTests && deployOptions.TestStandalone, automationOptions.RunTests ? "Disabled because Test Standalone is off" : "Disabled because Run Tests is off")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.BuildPlugin), "Building plugin", "Package the staged plugin into a distributable build")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.PrepareExample), "Preparing host project", "Assemble the example project used for packaging verification")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.ClangCheck), "Running Clang compile check", "Run the optional Clang validation against the packaged plugin").When(deployOptions.RunClangCompileCheck, "Disabled because Run Clang Compile Check is off")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.BuildExample), "Building example project with modules", "Compile the example project before packaging verification")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.PackageProjectPlugin), "Packaging code example project with plugin inside project", "Package the example project with the plugin installed at project level")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.TestProjectPlugin), "Testing code project package with project plugin", "Launch and validate the project-plugin package").When(automationOptions.RunTests && deployOptions.TestPackageWithProjectPlugin, automationOptions.RunTests ? "Disabled because Test Package With Project Plugin is off" : "Disabled because Run Tests is off")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.PrepareEnginePlugin), "Preparing to package example project with installed plugin", "Install the built plugin into the engine marketplace folder")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.PackageEnginePlugin), "Packaging code example project with installed plugin", "Package the example project with the plugin installed to the engine")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.TestEnginePlugin), "Testing code project package with installed plugin", "Launch and validate the engine-plugin package").When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Disabled because Test Package With Engine Plugin is off" : "Disabled because Run Tests is off")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.PackageBlueprint), "Packaging blueprint-only example project", "Package the blueprint-only project with the plugin installed to the engine")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.TestBlueprint), "Testing blueprint project package with installed plugin", "Launch and validate the blueprint-only package").When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Disabled because Test Package With Engine Plugin is off" : "Disabled because Run Tests is off")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.PackageDemo), "Packaging host project for demo", "Package the demo executable build")
                    .Step(DeployPluginExecutionIds.CreateDeployStepId(plugin, engineVersion, DeployPluginStepKeys.Archive), "Archiving", "Archive the requested deployment artifacts");
            }

            return plan.BuildPlan();
        }

        private async Task<global::LocalAutomation.Runtime.OperationResult> DeployForEngine(Engine engine, Microsoft.Extensions.Logging.ILogger logger, CancellationToken token)
        {
            DeployPluginForEngine deployForEngineOp = new() { Engine = engine };
            return await deployForEngineOp.Execute(UnrealOperationParameters, logger, token);
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
