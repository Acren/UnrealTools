using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core.IO;
using LocalAutomation.Extensions.Abstractions;
using Microsoft.Extensions.Logging;
using Semver;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    [Operation(SortOrder = 11)]
    public class VerifyDeployment : UnrealOperation<Plugin>
    {
        private sealed class VerificationState
        {
            public VerificationState(Plugin sourcePlugin, Engine engine, Project exampleProject, string tempPath, string packageOutputPath)
            {
                SourcePlugin = sourcePlugin ?? throw new ArgumentNullException(nameof(sourcePlugin));
                Engine = engine ?? throw new ArgumentNullException(nameof(engine));
                ExampleProject = exampleProject ?? throw new ArgumentNullException(nameof(exampleProject));
                TempPath = tempPath ?? throw new ArgumentNullException(nameof(tempPath));
                PackageOutputPath = packageOutputPath ?? throw new ArgumentNullException(nameof(packageOutputPath));
            }

            public Plugin SourcePlugin { get; }

            public Engine Engine { get; }

            public Project ExampleProject { get; }

            public string TempPath { get; }

            public string PackageOutputPath { get; }
        }

        /// <summary>
        /// Describes the deployment-verification subtree beneath the framework-owned root task.
        /// </summary>
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            IReadOnlyList<EngineVersion> enabledVersions = operationParameters.GetOptions<EngineVersionOptions>().EnabledVersions;
            List<EngineVersion> targetVersions = enabledVersions.Count > 0
                ? enabledVersions.ToList()
                : new List<EngineVersion> { plugin.EngineInstance.Version };
            AutomationOptions automationOptions = operationParameters.GetOptions<AutomationOptions>();
            root.Children(engines =>
            {
                foreach (EngineVersion engineVersion in targetVersions)
                {
                    EngineVersion currentEngineVersion = engineVersion;
                    engines
                        .Task($"UE {currentEngineVersion.MajorMinorString}", "Per-engine verification scope")
                        .Children(steps => steps
                            .Task("Prepare")
                                .Describe("Resolve the installed plugin and extract the matching example project archive")
                                .Run(context => PrepareVerificationState(context, currentEngineVersion))
                            .Then("Test Editor")
                                .Describe("Run the editor verification pass")
                                .When(automationOptions.RunTests, "Run Tests is off.")
                                .Run(TestEditorAsync)
                            .Then("Test Standalone")
                                .Describe("Run the standalone verification pass")
                                .When(automationOptions.RunTests, "Run Tests is off.")
                                .Run(TestStandaloneAsync)
                            .Then("Package Project")
                                .Describe("Package the example project with the installed plugin")
                                .Run(PackageProjectAsync)
                            .Then("Test Package")
                                .Describe("Run the packaged project verification pass")
                                .When(automationOptions.RunTests, "Run Tests is off.")
                                .Run(TestPackageAsync));
                }
            });
        }

        /// <summary>
        /// Deployment verification always needs engine-version selection, automation toggles, and example-project
        /// settings to know which builds to verify and how deeply to test them.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[]
                {
                    typeof(EngineVersionOptions),
                    typeof(AutomationOptions),
                    typeof(VerifyDeploymentOptions)
                });
        }

        private async Task PrepareVerificationState(global::LocalAutomation.Runtime.ExecutionTaskContext context, EngineVersion engineVersion)
        {
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters = context.ValidatedOperationParameters;
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = EngineFinder.GetEngineInstall(engineVersion)
                ?? throw new Exception($"Engine {engineVersion.MajorMinorString} not found");
            EngineVersion resolvedEngineVersion = engine.Version ?? throw new Exception("Engine version is not available");
            context.Logger.LogInformation($"Verifying plugin {plugin.Name} for {resolvedEngineVersion.MajorMinorString}");

            Plugin? installedPlugin = engine.FindInstalledPlugin(plugin.Name);

            if (installedPlugin is null)
            {
                throw new Exception($"Could not find plugin {plugin.Name} in engine located at {engine.TargetPath}");
            }

            string pluginVersionName = (plugin.PluginDescriptor ?? throw new Exception("Plugin descriptor is not loaded")).VersionName;
            context.Logger.LogInformation($"Source plugin version: {pluginVersionName}");
            string installedPluginVersionName = (installedPlugin.PluginDescriptor ?? throw new Exception("Installed plugin descriptor is not loaded")).VersionName;
            context.Logger.LogInformation($"Installed plugin version: {pluginVersionName}");

            if (!installedPluginVersionName.Contains(pluginVersionName))
            {
                throw new Exception($"Installed plugin version {installedPluginVersionName} does not include reference version {pluginVersionName}");
            }

            string exampleProjects = operationParameters.GetOptions<VerifyDeploymentOptions>().ExampleProjectsPath;
            string exampleProjectZip = FindExampleProjectZip(plugin, exampleProjects, engine);
            if (exampleProjectZip == null)
            {
                throw new Exception($"Could not find example project zip in {exampleProjects}");
            }

            context.Logger.LogInformation($"Identified {exampleProjectZip} as best example project");

            string temp = GetOperationTempPath(context);
            string exampleProjectTestPath = Path.Combine(temp, "ExampleProject");

            FileUtils.DeleteDirectoryIfExists(exampleProjectTestPath);
            ZipFile.ExtractToDirectory(exampleProjectZip, exampleProjectTestPath);

            Project exampleProject = new Project(exampleProjectTestPath);

            if (!exampleProject.IsValid)
            {
                throw new Exception($"Couldn't create project from {exampleProjectZip}");
            }

            string packageOutput = Path.Combine(temp, "Package");
            FileUtils.DeleteDirectoryIfExists(packageOutput);

            context.SetOperationData(new VerificationState(plugin, engine, exampleProject, temp, packageOutput));
            await Task.CompletedTask;
        }

        private async Task TestEditorAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            VerificationState state = context.GetOperationData<VerificationState>();
            AutomationOptions automationOptions = context.ValidatedOperationParameters.GetOptions<AutomationOptions>();

            context.Logger.LogInformation("Launching and testing example project editor");
            await RunExampleProjectOperationAsync<LaunchProjectEditor>(state, automationOptions, context, "Failed to launch example project");
        }

        private async Task TestStandaloneAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            VerificationState state = context.GetOperationData<VerificationState>();
            AutomationOptions automationOptions = context.ValidatedOperationParameters.GetOptions<AutomationOptions>();

            context.Logger.LogInformation("Launching and testing standalone");
            await RunExampleProjectOperationAsync<LaunchStandalone>(state, automationOptions, context, "Failed to launch standalone");
        }

        private async Task PackageProjectAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            VerificationState state = context.GetOperationData<VerificationState>();
            await RunChildOperationAsync(new PackageProject(), CreateExampleProjectParams(state, outputPathOverride: state.PackageOutputPath), context, required: true, failureMessage: "Failed to package example project", hideChildOperationRootInGraph: true);
        }

        private async Task TestPackageAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            VerificationState state = context.GetOperationData<VerificationState>();
            AutomationOptions automationOptions = context.ValidatedOperationParameters.GetOptions<AutomationOptions>();

            await RunExampleProjectOperationAsync<LaunchStagedPackage>(state, automationOptions, context, "Launch and test package failed");

            context.Logger.LogInformation($"Finished verifying plugin {state.SourcePlugin.Name} for {state.Engine.Version.MajorMinorString}");
        }

        private global::LocalAutomation.Runtime.OperationParameters CreateExampleProjectParams(VerificationState state, AutomationOptions? automationOptions = null, string? outputPathOverride = null)
        {
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = state.ExampleProject;
            parameters.OutputPathOverride = outputPathOverride;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            if (automationOptions != null)
            {
                parameters.SetOptions(automationOptions);
            }

            return parameters;
        }

        private Task RunExampleProjectOperationAsync<TOperation>(VerificationState state, AutomationOptions automationOptions, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage)
            where TOperation : global::LocalAutomation.Runtime.Operation, new()
        {
            return RunChildOperationAsync<TOperation>(CreateExampleProjectParams(state, automationOptions), context, required: true, failureMessage: failureMessage, hideChildOperationRootInGraph: true);
        }

        public readonly struct ExampleProjectZipInfo
        {
            public readonly string Path;
            public readonly string PluginName;
            public readonly SemVersion? PluginVersion;
            public readonly SemVersion? EngineVersion;
            public readonly bool IsExampleProject;

            public ExampleProjectZipInfo(string path)
            {
                Path = path;
                PluginName = string.Empty;
                PluginVersion = null;
                EngineVersion = null;
                string zipName = System.IO.Path.GetFileNameWithoutExtension(path);

                // Expect zip to be named PluginName_PluginVersion_EngineVersion_ExampleProject.zip
                string[] split = zipName.Split('_');
                IsExampleProject = split.Length > 3 && split[3] == "ExampleProject";
                if (!IsExampleProject)
                {
                    return;
                }
                PluginName = split[0];
                PluginVersion = split.Length > 1 ? SemVersion.Parse(split[1], SemVersionStyles.Any) : null;
                EngineVersion = split.Length > 2 ? SemVersion.Parse(split[2].Replace("UE", ""), SemVersionStyles.Any) : null;
            }
        }

        private string FindExampleProjectZip(Plugin plugin, string exampleProjectsPath, Engine engine)
        {
            SemVersion pluginVersion = (plugin.PluginDescriptor ?? throw new Exception("Plugin descriptor is not loaded")).SemVersion;
            string exampleProjects = exampleProjectsPath;
            string extension = "*.zip";
            string[] zipPaths = Directory.GetFiles(exampleProjects, extension, SearchOption.AllDirectories);
            List<ExampleProjectZipInfo> pluginExampleProjectZips = new();

            foreach (string zipPath in zipPaths)
            {
                ExampleProjectZipInfo zipInfo = new(zipPath);
                if (zipInfo.IsExampleProject && zipInfo.PluginName == plugin.Name)
                {
                    pluginExampleProjectZips.Add(zipInfo);
                }
            }

            if (pluginExampleProjectZips.Count == 0)
            {
                throw new Exception("No valid zips");
            }

            ExampleProjectZipInfo selectedZip = pluginExampleProjectZips
                .Where(z => z.PluginVersion != null && SemVersion.CompareSortOrder(z.PluginVersion, pluginVersion) <= 0 && z.EngineVersion != null && SemVersion.CompareSortOrder(z.EngineVersion, engine.SemVersion) <= 0)
                .OrderByDescending(z => z.PluginVersion)
                .ThenByDescending(z => z.EngineVersion)
                .First();

            return selectedZip.Path;
        }
    }
}
