using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Polly;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    internal sealed class DeployPreparedSourceState
    {
        public DeployPreparedSourceState(Plugin sourcePlugin, Project hostProject)
        {
            SourcePlugin = sourcePlugin ?? throw new ArgumentNullException(nameof(sourcePlugin));
            HostProject = hostProject ?? throw new ArgumentNullException(nameof(hostProject));
        }

        public Plugin SourcePlugin { get; }

        public Project HostProject { get; }
    }

    public class DeployPluginForEngine : UnrealOperation<Plugin>
    {
        private sealed class DeploymentState
        {
            public DeploymentState(Engine engine, Plugin sourcePlugin, Project hostProject, string workspacePath, Project workspaceProject, Plugin workspacePlugin, Plugin? stagingPlugin = null, Plugin? builtPlugin = null, Project? exampleProject = null, Package? demoPackage = null)
            {
                Engine = engine ?? throw new ArgumentNullException(nameof(engine));
                SourcePlugin = sourcePlugin ?? throw new ArgumentNullException(nameof(sourcePlugin));
                HostProject = hostProject ?? throw new ArgumentNullException(nameof(hostProject));
                WorkspacePath = workspacePath ?? throw new ArgumentNullException(nameof(workspacePath));
                WorkspaceProject = workspaceProject ?? throw new ArgumentNullException(nameof(workspaceProject));
                WorkspacePlugin = workspacePlugin ?? throw new ArgumentNullException(nameof(workspacePlugin));
                StagingPlugin = stagingPlugin;
                BuiltPlugin = builtPlugin;
                ExampleProject = exampleProject;
                DemoPackage = demoPackage;
            }

            public Engine Engine { get; }

            public Plugin SourcePlugin { get; }

            public Project HostProject { get; }

            public string WorkspacePath { get; }

            public Project WorkspaceProject { get; }

            public Plugin WorkspacePlugin { get; }

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
                return new DeploymentState(Engine, SourcePlugin, HostProject, WorkspacePath, WorkspaceProject, WorkspacePlugin, stagingPlugin, BuiltPlugin, ExampleProject, DemoPackage);
            }

            public DeploymentState WithBuiltPlugin(Plugin builtPlugin)
            {
                return new DeploymentState(Engine, SourcePlugin, HostProject, WorkspacePath, WorkspaceProject, WorkspacePlugin, StagingPlugin, builtPlugin, ExampleProject, DemoPackage);
            }

            public DeploymentState WithExampleProject(Project exampleProject)
            {
                return new DeploymentState(Engine, SourcePlugin, HostProject, WorkspacePath, WorkspaceProject, WorkspacePlugin, StagingPlugin, BuiltPlugin, exampleProject, DemoPackage);
            }

            public DeploymentState WithDemoPackage(Package demoPackage)
            {
                return new DeploymentState(Engine, SourcePlugin, HostProject, WorkspacePath, WorkspaceProject, WorkspacePlugin, StagingPlugin, BuiltPlugin, ExampleProject, demoPackage);
            }
        }

        /// <summary>
        /// Gets the isolated per-engine temp root so multiple engine-specific execution scopes can run without colliding
        /// in shared staging or package folders.
        /// </summary>
        private string GetEngineTempPath(global::LocalAutomation.Runtime.ExecutionTaskContext context, Engine engine)
        {
            return Path.Combine(base.GetOperationTempPath(context), $"UE_{engine.Version.MajorMinorString}");
        }

        private static string GetWorkspacePath(string workspaceRootPath, string label)
        {
            return Path.Combine(workspaceRootPath, global::LocalAutomation.Runtime.ExecutionPathConventions.MakeCompactSegment(label));
        }

        private static string GetWorkspacePath(string workspaceRootPath, string label, string leafName)
        {
            return Path.Combine(GetWorkspacePath(workspaceRootPath, label), leafName);
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
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            AutomationOptions automationOptions = operationParameters.GetOptions<AutomationOptions>();
            PluginDeployOptions deployOptions = operationParameters.GetOptions<PluginDeployOptions>();

            root.Children(steps => steps
                .Task("Prepare Workspace")
                    .Describe("Create the isolated engine-specific workspace from the prepared source")
                    .Run(context => PrepareStepAsync(context))
                .Then("Stage Plugin")
                    .Describe("Create the staged plugin copy used for packaging and archiving")
                    .Run(context => StagingStepAsync(context))
                .Then("Build Editor")
                    .Describe("Compile the host project editor before validation runs")
                    .Run(context => BuildEditor(context))
                .Then("Test Editor")
                    .Describe("Run the editor automation validation pass")
                    .When(automationOptions.RunTests, "Run Tests is off.")
                    .Run(context => TestEditor(context, automationOptions))
                .Then("Test Standalone")
                    .Describe("Run the standalone validation pass")
                    .When(automationOptions.RunTests && deployOptions.TestStandalone, automationOptions.RunTests ? "Test Standalone is off." : "Run Tests is off.")
                    .Run(context => TestStandalone(context, automationOptions))
                .Then("Build Plugin")
                    .Describe("Package the staged plugin into a distributable build")
                    .Run(context => BuildPlugin(context))
                .Then("Prepare Example")
                    .Describe("Assemble the example project used for packaging verification")
                    .Run(context => PrepareExampleProject(context))
                .Then("Clang Check")
                    .Describe("Run the optional Clang validation against the packaged plugin")
                    .When(deployOptions.RunClangCompileCheck, "Run Clang Compile Check is off.")
                    .Run(context => RunClangCompileCheck(context))
                .Then("Build Example")
                    .Describe("Compile the example project before packaging verification")
                    .Run(context => BuildCodeExampleProject(context))
                .Then("Package Project Plugin")
                    .Describe("Package the example project with the plugin installed at project level")
                    .Run(context => PackageCodeExampleProjectWithProjectPluginAsync(context, automationOptions))
                .Then("Test Project Plugin")
                    .Describe("Launch and validate the project-plugin package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithProjectPlugin, automationOptions.RunTests ? "Test Package With Project Plugin is off." : "Run Tests is off.")
                    .Run(context => TestCodeExampleProjectPackageWithProjectPluginAsync(context, automationOptions))
                .Then("Install Engine Plugin")
                    .Describe("Install the built plugin into the engine marketplace folder")
                    .Run(context => PrepareEnginePluginInstallAsync(context))
                .Then("Package Engine Plugin")
                    .Describe("Package the example project with the plugin installed to the engine")
                    .Run(context => PackageCodeExampleProjectWithEnginePluginAsync(context, automationOptions))
                .Then("Test Engine Plugin")
                    .Describe("Launch and validate the engine-plugin package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Run(context => TestCodeExampleProjectPackageWithEnginePluginAsync(context, automationOptions))
                .Then("Package Blueprint")
                    .Describe("Package the blueprint-only project with the plugin installed to the engine")
                    .Run(context => PackageBlueprintExampleProjectWithEnginePluginAsync(context, automationOptions))
                .Then("Test Blueprint")
                    .Describe("Launch and validate the blueprint-only package")
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Run(context => TestBlueprintExampleProjectPackageWithEnginePluginAsync(context, automationOptions))
                .Then("Package Demo")
                    .Describe("Package the demo executable build")
                    .Run(context => PackageDemoExecutable(context))
                .Then("Archive")
                    .Describe("Archive the requested deployment artifacts")
                    .Run(context => ArchiveArtifacts(context)));
        }

        /// <summary>
        /// Per-engine deployment reuses the same option groups as the outer deployment flow because it reads the shared
        /// deployment settings directly while orchestrating child operations.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(AutomationOptions),
                    typeof(PluginBuildOptions),
                    typeof(PluginDeployOptions)
                });
        }

        private async Task BuildEditor(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building host project editor");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            global::LocalAutomation.Runtime.OperationParameters buildEditorParams = CreateParameters();
            buildEditorParams.Target = state.WorkspaceProject;
            buildEditorParams.OutputPathOverride = GetWorkspacePath(state.WorkspacePath, "BuildEditorOutput");
            buildEditorParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };

            await RunChildOperationAsync<BuildEditor>(buildEditorParams, context, required: true, failureMessage: "Failed to build host project editor");
        }

        /// <summary>
        /// Runs the scheduler-backed prepare step.
        /// </summary>
        private async Task PrepareStepAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing engine workspace");
            global::LocalAutomation.Runtime.ValidatedOperationParameters validatedParameters = context.ValidatedOperationParameters;
            Engine engine = GetTargetEngineInstall(validatedParameters)
                ?? throw new Exception("Engine not specified");
            DeployPreparedSourceState preparedSource = context.GetState<DeployPreparedSourceState>();
            Plugin plugin = preparedSource.SourcePlugin;
            Project hostProject = preparedSource.HostProject;

            context.Logger.LogInformation($"Engine version: {engine.Version}");
            string workspacePath = GetEngineTempPath(context, engine);
            string archivePrefix = BuildArchivePrefix(new DeploymentState(engine, plugin, hostProject, workspacePath, hostProject, plugin));
            context.Logger.LogInformation($"Archive name prefix is '{archivePrefix}'");
            context.Logger.LogInformation($"Source host project: {hostProject.ProjectPath}");
            context.Logger.LogInformation($"Source plugin: {plugin.PluginPath}");
            context.Logger.LogInformation($"Workspace root: {workspacePath}");

            if (!Directory.Exists(hostProject.PluginsPath))
            {
                throw new DirectoryNotFoundException($"Host project is missing required Plugins directory: {hostProject.PluginsPath}");
            }

            context.Logger.LogInformation($"Source Plugins directory: {hostProject.PluginsPath}");

            context.Logger.LogInformation($"Deleting existing workspace root: {workspacePath}");
            FileUtils.DeleteDirectoryIfExists(workspacePath);
            context.Logger.LogInformation($"Creating workspace root: {workspacePath}");
            Directory.CreateDirectory(workspacePath);

            string workspaceProjectPath = GetWorkspacePath(workspacePath, "HostProject");
            context.Logger.LogInformation($"Copying host project to workspace: {workspaceProjectPath}");
            FileUtils.MaterializeDirectory(hostProject.ProjectPath, workspaceProjectPath, MaterializationSpecs.CreateProject(hostProject));

            string workspacePluginsPath = Path.Combine(workspaceProjectPath, "Plugins");
            Directory.CreateDirectory(workspacePluginsPath);
            string workspacePluginPath = Path.Combine(workspacePluginsPath, plugin.Name);
            context.Logger.LogInformation($"Materializing target plugin into workspace: {workspacePluginPath}");
            FileUtils.MaterializeDirectory(plugin.PluginPath, workspacePluginPath, MaterializationSpecs.CreatePlugin(plugin));
            context.Logger.LogInformation($"Finished copying host project to workspace: {workspaceProjectPath}");

            if (!Directory.Exists(workspacePluginsPath))
            {
                throw new DirectoryNotFoundException($"Workspace project copy is missing Plugins directory after copy: {workspacePluginsPath}");
            }

            context.Logger.LogInformation($"Workspace Plugins directory: {workspacePluginsPath}");

            Project workspaceProject = new(workspaceProjectPath);
            context.Logger.LogInformation($"Enumerating workspace plugins in: {workspacePluginsPath}");

            Plugin workspacePlugin = workspaceProject.Plugins.SingleOrDefault(projectPlugin => projectPlugin.Name == plugin.Name)
                ?? throw new Exception($"Could not find plugin '{plugin.Name}' inside engine workspace project");
            context.Logger.LogInformation($"Resolved workspace plugin: {workspacePlugin.PluginPath}");

            UpdateProjectDescriptorForArchive(new DeploymentState(engine, plugin, hostProject, workspacePath, workspaceProject, workspacePlugin), workspaceProject);
            context.Logger.LogInformation("Updated workspace project descriptor for archive output");
            context.SetOperationState(new DeploymentState(engine, plugin, hostProject, workspacePath, workspaceProject, workspacePlugin));
            context.Logger.LogInformation("Stored deployment state for engine workspace");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Runs the scheduler-backed staging step.
        /// </summary>
        private async Task StagingStepAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing plugin staging copy");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            string stagingPluginPath = GetWorkspacePath(state.WorkspacePath, "PluginStaging", state.SourcePlugin.Name);
            context.Logger.LogInformation($"Engine version: {state.Engine.Version}");
            context.Logger.LogInformation($"Workspace plugin: {state.WorkspacePlugin.PluginPath}");
            context.Logger.LogInformation($"Staging destination: {stagingPluginPath}");
            FileUtils.DeleteDirectoryIfExists(stagingPluginPath);
            FileUtils.MaterializeDirectory(state.WorkspacePlugin.PluginPath, stagingPluginPath, MaterializationSpecs.CreatePlugin(state.WorkspacePlugin));
            context.Logger.LogInformation($"Copied plugin to staging destination: {stagingPluginPath}");
            Plugin stagingPlugin = new(stagingPluginPath);
            UpdatePluginDescriptorForArchive(state, stagingPlugin);
            context.SetOperationState(state.WithStagingPlugin(stagingPlugin));
            context.Logger.LogInformation($"Updated plugin descriptor for staging: {stagingPlugin.PluginDescriptor.VersionName}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Builds the archive filename prefix for the active engine-specific execution scope.
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
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.TestEditor")
                .SetTag("trigger", "StepTransition");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            global::LocalAutomation.Runtime.OperationParameters launchEditorParams = CreateParameters();
            launchEditorParams.Target = state.WorkspaceProject;
            launchEditorParams.OutputPathOverride = GetWorkspacePath(state.WorkspacePath, "LaunchEditorOutput");
            launchEditorParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            launchEditorParams.SetOptions(automationOptions);

            await RunChildOperationAsync<LaunchProjectEditor>(launchEditorParams, context, required: true, failureMessage: "Failed to launch host project");
            activity.SetTag("result", "Completed");
        }

        private async Task TestStandalone(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Launching and testing standalone");
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.TestStandalone")
                .SetTag("trigger", "StepTransition");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            global::LocalAutomation.Runtime.OperationParameters launchStandaloneParams = CreateParameters();
            launchStandaloneParams.Target = state.WorkspaceProject;
            launchStandaloneParams.OutputPathOverride = GetWorkspacePath(state.WorkspacePath, "LaunchStandaloneOutput");
            launchStandaloneParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            launchStandaloneParams.SetOptions(automationOptions);

            await RunChildOperationAsync<LaunchStandalone>(launchStandaloneParams, context, required: true, failureMessage: "Failed to launch standalone");
            activity.SetTag("result", "Completed");
        }

        // Package the staged plugin into a distributable output before deployment verification continues.
        private async Task BuildPlugin(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building plugin");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            Plugin stagingPlugin = state.GetRequiredStagingPlugin();
            string pluginBuildPath = GetWorkspacePath(state.WorkspacePath, "PluginBuild", state.SourcePlugin.Name);
            FileUtils.DeleteDirectoryIfExists(pluginBuildPath);
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.BuildPlugin")
                .SetTag("plugin.name", state.SourcePlugin.Name)
                .SetTag("engine.version", state.Engine.Version.ToString())
                .SetTag("output.path", pluginBuildPath)
                .SetTag("trigger", "StepTransition");

            global::LocalAutomation.Runtime.OperationParameters buildPluginParams = CreateParameters();
            buildPluginParams.Target = stagingPlugin;
            buildPluginParams.OutputPathOverride = pluginBuildPath;
            buildPluginParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            buildPluginParams.SetOptions(context.ValidatedOperationParameters.GetOptions<PluginBuildOptions>());

            using (PerformanceActivityScope childRunActivity = PerformanceTelemetry.StartActivity("DeployPlugin.BuildPlugin.RunChild"))
            {
                await RunChildOperationAsync<PackagePlugin>(buildPluginParams, context, required: true, failureMessage: "Plugin build failed");
                childRunActivity.SetTag("child.operation", nameof(PackagePlugin));
            }

            Plugin builtPlugin;
            using (PerformanceActivityScope materializeActivity = PerformanceTelemetry.StartActivity("DeployPlugin.BuildPlugin.MaterializeBuiltPlugin"))
            {
                materializeActivity.SetTag("plugin.path", pluginBuildPath);
                builtPlugin = new(pluginBuildPath);
            }

            using (PerformanceActivityScope sharedStateActivity = PerformanceTelemetry.StartActivity("DeployPlugin.BuildPlugin.StoreSharedState"))
            {
                context.SetOperationState(state.WithBuiltPlugin(builtPlugin));
            }

            context.Logger.LogInformation("Plugin build complete");
        }

        private async Task PrepareExampleProject(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing host project");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            Project hostProject = state.WorkspaceProject;
            Plugin plugin = state.SourcePlugin;
            Plugin builtPlugin = state.GetRequiredBuiltPlugin();
            Engine engine = state.Engine;
            string uProjectFilename = Path.GetFileName(hostProject.UProjectPath);
            string projectName = Path.GetFileNameWithoutExtension(hostProject.UProjectPath);

            string exampleProjectPath = GetWorkspacePath(state.WorkspacePath, "ExampleProject");

            FileUtils.DeleteDirectoryIfExists(exampleProjectPath);

            FileUtils.MaterializeDirectory(hostProject.ProjectPath, exampleProjectPath, MaterializationSpecs.CreateProject(hostProject));

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
            context.SetOperationState(state.WithExampleProject(exampleProject));
            await Task.CompletedTask;
        }

        private async Task BuildCodeExampleProject(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            context.Logger.LogInformation("Building example project with modules");
            DeploymentState state = context.GetOperationState<DeploymentState>();

            global::LocalAutomation.Runtime.OperationParameters buildExampleProjectParams = CreateParameters();
            buildExampleProjectParams.Target = state.GetRequiredExampleProject();
            buildExampleProjectParams.OutputPathOverride = GetWorkspacePath(state.WorkspacePath, "BuildExampleOutput");
            buildExampleProjectParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            await RunChildOperationAsync<BuildEditor>(buildExampleProjectParams, context, required: true, failureMessage: "Failed to build example project with modules");
        }

        // Reuse the example project's prebuilt editor binaries so the packaging validation passes do not spend time
        // recompiling the editor target before cooking and staging.
        private global::LocalAutomation.Runtime.OperationParameters CreateExampleProjectPackageParams(DeploymentState state, global::LocalAutomation.Runtime.ExecutionTaskContext context, string outputPath)
        {
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = state.GetRequiredExampleProject();
            parameters.OutputPathOverride = outputPath;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
            return parameters;
        }

        private global::LocalAutomation.Runtime.OperationParameters CreatePackageLaunchParams(Package package, Engine engine, AutomationOptions automationOptions, string outputPath)
        {
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = package;
            parameters.OutputPathOverride = outputPath;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            parameters.SetOptions(automationOptions);
            return parameters;
        }

        private Task RunPackageProjectAsync(DeploymentState state, string outputPath, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage)
        {
            FileUtils.DeleteDirectoryIfExists(outputPath);
            return RunChildOperationAsync<PackageProject>(CreateExampleProjectPackageParams(state, context, outputPath), context, required: true, failureMessage: failureMessage);
        }

        private Task RunLaunchPackageAsync(Package package, Engine engine, AutomationOptions automationOptions, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage, string outputPath)
        {
            return RunChildOperationAsync<LaunchPackage>(CreatePackageLaunchParams(package, engine, automationOptions, outputPath), context, required: true, failureMessage: failureMessage);
        }

        // Rebuild the packaged plugin in-place with Clang so validation matches the project-plugin flow Fab uses.
        private async Task RunClangCompileCheck(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Running Clang compile check");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.RunClangCompileCheck")
                .SetTag("plugin.name", state.SourcePlugin.Name)
                .SetTag("engine.version", state.Engine.Version.ToString());
            Project exampleProject = state.GetRequiredExampleProject();
            Plugin builtPlugin = state.GetRequiredBuiltPlugin();
            Plugin exampleProjectPlugin = exampleProject.Plugins.SingleOrDefault(plugin => plugin.Name == builtPlugin.Name);
            if (exampleProjectPlugin == null)
            {
                activity.SetTag("plugin.present_in_example", false);
                throw new Exception("Could not find packaged plugin inside example project for Clang validation");
            }

            activity.SetTag("plugin.present_in_example", true)
                .SetTag("example.plugin.path", exampleProjectPlugin.PluginPath);

            global::LocalAutomation.Runtime.OperationParameters clangBuildParams = CreateParameters();
            clangBuildParams.Target = exampleProjectPlugin;
            clangBuildParams.OutputPathOverride = GetWorkspacePath(state.WorkspacePath, "ClangCheckOutput");
            clangBuildParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };

            clangBuildParams.SetOptions(new BuildConfigurationOptions
            {
                Configuration = BuildConfiguration.Development
            });
            clangBuildParams.SetOptions(new UbtCompilerOptions
            {
                Compiler = UbtCompiler.Clang
            });

            await RunChildOperationAsync<BuildPlugin>(clangBuildParams, context, required: true, failureMessage: "Clang compile check failed");
            activity.SetTag("child.result", "Succeeded");
        }

        /// <summary>
        /// Scheduler wrapper for packaging the code example project with the plugin installed at project level.
        /// </summary>
        private async Task PackageCodeExampleProjectWithProjectPluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging code example project with plugin inside project");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            string projectPluginPackagePath = GetWorkspacePath(state.WorkspacePath, "ProjectPluginPackage");
            await RunPackageProjectAsync(state, projectPluginPackagePath, context, "Package project with included plugin failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the project-plugin package.
        /// </summary>
        private async Task TestCodeExampleProjectPackageWithProjectPluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing code project package with project plugin");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            string projectPluginPackagePath = GetWorkspacePath(state.WorkspacePath, "ProjectPluginPackage");
            Package projectPluginPackage = new(Path.Combine(projectPluginPackagePath, state.Engine.GetWindowsPlatformName()));
            await RunLaunchPackageAsync(projectPluginPackage, state.Engine, automationOptions, context, "Launch and test with project plugin failed", GetWorkspacePath(state.WorkspacePath, "ProjectPluginLaunchOutput"));
        }

        /// <summary>
        /// Scheduler wrapper for installing the built plugin into the engine marketplace folder.
        /// </summary>
        private async Task PrepareEnginePluginInstallAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing to package example project with installed plugin");
            DeploymentState state = context.GetOperationState<DeploymentState>();
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
            DeploymentState state = context.GetOperationState<DeploymentState>();
            state.GetRequiredExampleProject().RemovePlugin(state.SourcePlugin.Name);
            string enginePluginPackagePath = GetWorkspacePath(state.WorkspacePath, "EnginePluginPackage");
            await RunPackageProjectAsync(state, enginePluginPackagePath, context, "Package project with engine plugin failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the engine-plugin package.
        /// </summary>
        private async Task TestCodeExampleProjectPackageWithEnginePluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing code project package with installed plugin");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            string enginePluginPackagePath = GetWorkspacePath(state.WorkspacePath, "EnginePluginPackage");
            Package enginePluginPackage = new(Path.Combine(enginePluginPackagePath, state.Engine.GetWindowsPlatformName()));
            await RunLaunchPackageAsync(enginePluginPackage, state.Engine, automationOptions, context, "Launch and test with installed plugin failed", GetWorkspacePath(state.WorkspacePath, "EnginePluginLaunchOutput"));
        }

        /// <summary>
        /// Scheduler wrapper for packaging the blueprint-only example project.
        /// </summary>
        private async Task PackageBlueprintExampleProjectWithEnginePluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging blueprint-only example project");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            state.GetRequiredExampleProject().ConvertToBlueprintOnly();
            PreparePluginsForProject(state, state.GetRequiredExampleProject(), context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>());
            string blueprintOnlyPackagePath = GetWorkspacePath(state.WorkspacePath, "BlueprintOnlyPackage");
            await RunPackageProjectAsync(state, blueprintOnlyPackagePath, context, "Package blueprint-only project failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged blueprint-only example project.
        /// </summary>
        private async Task TestBlueprintExampleProjectPackageWithEnginePluginAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing blueprint project package with installed plugin");
            DeploymentState state = context.GetOperationState<DeploymentState>();
            string blueprintOnlyPackagePath = GetWorkspacePath(state.WorkspacePath, "BlueprintOnlyPackage");
            Package package = new(Path.Combine(blueprintOnlyPackagePath, state.Engine.GetWindowsPlatformName()));
            await RunLaunchPackageAsync(package, state.Engine, automationOptions, context, "Launch and test blueprint project with installed plugin failed", GetWorkspacePath(state.WorkspacePath, "BlueprintLaunchOutput"));
        }

        private async Task PackageDemoExecutable(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            DeploymentState state = context.GetOperationState<DeploymentState>();
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
            string demoPackagePath = GetWorkspacePath(state.WorkspacePath, "DemoExe");

            FileUtils.DeleteDirectoryIfExists(demoPackagePath);

            global::LocalAutomation.Runtime.OperationParameters demoPackageParams = CreateExampleProjectPackageParams(state, context, demoPackagePath);

            demoPackageParams.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
            demoPackageParams.GetOptions<PackageOptions>().NoDebugInfo = true;

            global::LocalAutomation.Runtime.OperationResult demoExePackageOperationResult = await RunChildOperationAsync<PackageProject>(demoPackageParams, context);

            if (!demoExePackageOperationResult.Success)
            {
                throw new Exception("Example project build failed");
            }

            context.SetOperationState(state.WithDemoPackage(new Package(Path.Combine(demoPackagePath, engine.GetWindowsPlatformName()))));
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
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            DeploymentState state = context.GetOperationState<DeploymentState>();
            Plugin plugin = state.SourcePlugin;
            Plugin builtPlugin = state.GetRequiredBuiltPlugin();
            Project exampleProject = state.GetRequiredExampleProject();
            Package demoPackage = state.GetRequiredDemoPackage();
            Plugin stagingPlugin = state.GetRequiredStagingPlugin();
            string archivePrefix = BuildArchivePrefix(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");

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
            string pluginSourcePath = GetWorkspacePath(state.WorkspacePath, "PluginSource", plugin.Name);
            FileUtils.DeleteDirectoryIfExists(pluginSourcePath);
            FileUtils.MaterializeDirectory(stagingPlugin.PluginPath, pluginSourcePath, MaterializationSpecs.CreatePlugin(stagingPlugin));

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
        protected override string? CheckRequirementsSatisfied(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            EngineVersionOptions engineVersionOptions = operationParameters.GetOptions<EngineVersionOptions>();
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

                string? platformRequirementsError = PluginBuildPlatformValidation.CheckRequirementsSatisfied(operationParameters, engine);
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
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            IReadOnlyList<EngineVersion> enabledVersions = operationParameters.GetOptions<EngineVersionOptions>().EnabledVersions;
            List<EngineVersion> targetVersions = enabledVersions.Count > 0
                ? enabledVersions.ToList()
                : new List<EngineVersion> { plugin.EngineInstance.Version };

            /* Shared source preparation is an authored deploy step rather than an implicit callback on the root so it
               stays visible in the graph and remains the explicit predecessor of the per-engine branches. */
            root.Child("Prepare Shared Source", "Apply shared source-tree mutations once before engine-specific workspaces are created")
                .Run(PrepareSharedSourceAsync);

            root.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, engines =>
            {
                foreach (EngineVersion engineVersion in targetVersions)
                {
                    engines
                        .Task($"UE {engineVersion.MajorMinorString}", "Per-engine deployment scope")
                        .AddChildOperation<DeployPluginForEngine>(
                            "Deploy Plugin For Engine",
                            () => CreateEngineParameters(operationParameters.CreateChild(), engineVersion),
                            "Per-engine deployment execution subtree")
                            .HideInGraph();
                }
            });
        }

        private async Task PrepareSharedSourceAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing shared plugin source");
            global::LocalAutomation.Runtime.ValidatedOperationParameters validatedParameters = context.ValidatedOperationParameters;
            Plugin plugin = GetRequiredTarget(validatedParameters);
            PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
            Project hostProject = plugin.HostProject;
            ProjectDescriptor projectDescriptor = hostProject.ProjectDescriptor;

            if (!projectDescriptor.HasPluginEnabled(plugin.Name))
            {
                throw new Exception("Host project must have plugin enabled");
            }

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

                if (firstLine == expectedComment)
                {
                    continue;
                }

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

            hostProject.SetProjectVersion(plugin.PluginDescriptor.VersionName, context.Logger);
            context.SetOperationState(new DeployPreparedSourceState(plugin, hostProject));
            await Task.CompletedTask;
        }

        private global::LocalAutomation.Runtime.OperationParameters CreateEngineParameters(global::LocalAutomation.Runtime.OperationParameters parentParameters, EngineVersion engineVersion)
        {
            global::LocalAutomation.Runtime.OperationParameters childParameters = parentParameters.CreateChild();
            childParameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engineVersion };
            return childParameters;
        }

        /// <summary>
        /// Plugin deployment exposes engine selection, automation toggles, plugin build settings, and deployment
        /// packaging controls so the user can configure the full archive/test flow up front.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(EngineVersionOptions),
                    typeof(AutomationOptions),
                    typeof(PluginBuildOptions),
                    typeof(PluginDeployOptions)
                });
        }

        protected override bool FailOnWarning()
        {
            return true;
        }

    }
}
