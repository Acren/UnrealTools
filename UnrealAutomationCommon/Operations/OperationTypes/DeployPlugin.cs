using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LocalAutomation.Core;
using LocalAutomation.Core.IO;
using LocalAutomation.Extensions.Abstractions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Polly;
using UnrealAutomationCommon.Operations;
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
        private sealed class DeploymentWorkspaceState
        {
            public DeploymentWorkspaceState(Engine engine, Plugin sourcePlugin, Project hostProject, string workspacePath, string workspaceProjectPath, string workspacePluginPath)
            {
                Engine = engine ?? throw new ArgumentNullException(nameof(engine));
                SourcePlugin = sourcePlugin ?? throw new ArgumentNullException(nameof(sourcePlugin));
                HostProject = hostProject ?? throw new ArgumentNullException(nameof(hostProject));
                WorkspacePath = workspacePath ?? throw new ArgumentNullException(nameof(workspacePath));
                WorkspaceProjectPath = workspaceProjectPath ?? throw new ArgumentNullException(nameof(workspaceProjectPath));
                WorkspacePluginPath = workspacePluginPath ?? throw new ArgumentNullException(nameof(workspacePluginPath));
            }

            public Engine Engine { get; }

            public Plugin SourcePlugin { get; }

            public Project HostProject { get; }

            public string WorkspacePath { get; }

            public string WorkspaceProjectPath { get; }

            public string WorkspacePluginPath { get; }
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

        private void UpdatePluginDescriptorForArchive(DeploymentWorkspaceState state, Plugin plugin)
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

        private void UpdateProjectDescriptorForArchive(DeploymentWorkspaceState state, Project project)
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
        /// Keeps every derived artifact path under the engine-specific workspace so later branch tasks can reconstruct the
        /// same project, plugin, and package locations without mutating shared operation state.
        /// </summary>
        private static string GetWorkspaceProjectPath(DeploymentWorkspaceState state)
        {
            return state.WorkspaceProjectPath;
        }

        /// <summary>
        /// Returns the materialized workspace plugin path created during the initial engine-specific workspace step.
        /// </summary>
        private static string GetWorkspacePluginPath(DeploymentWorkspaceState state)
        {
            return state.WorkspacePluginPath;
        }

        /// <summary>
        /// Returns the staging plugin path used for the source-style archive and packaged plugin build input.
        /// </summary>
        private static string GetStagingPluginPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "PluginStaging", state.SourcePlugin.Name);
        }

        /// <summary>
        /// Returns the packaged plugin output path produced by the plugin-build step.
        /// </summary>
        private static string GetBuiltPluginPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "PluginBuild", state.SourcePlugin.Name);
        }

        /// <summary>
        /// Returns the prepared-and-prebuilt project-plugin base that all later packaging variants clone.
        /// </summary>
        private static string GetExampleProjectBasePath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "ExampleProjectBase");
        }

        /// <summary>
        /// Returns the full-copy project variant used for the engine-plugin packaging branch.
        /// </summary>
        private static string GetEnginePluginVariantPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "EnginePluginVariant");
        }

        /// <summary>
        /// Returns the full-copy project variant used only for the Clang validation branch.
        /// </summary>
        private static string GetClangVariantPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "ClangVariant");
        }

        /// <summary>
        /// Returns the full-copy project variant used for the blueprint packaging and demo packaging branch.
        /// </summary>
        private static string GetBlueprintDemoVariantPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "BlueprintDemoVariant");
        }

        /// <summary>
        /// Returns the Deploy Plugin child-operation output directory used for logs and reports for the project-plugin
        /// branch. The packaged build itself now comes from the prepared project's staged-build directory.
        /// </summary>
        private static string GetProjectPluginOperationOutputPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "ProjectPluginPackage");
        }

        /// <summary>
        /// Returns the Deploy Plugin child-operation output directory used for logs and reports for the engine-plugin
        /// branch. The packaged build itself now comes from the prepared project's staged-build directory.
        /// </summary>
        private static string GetEnginePluginOperationOutputPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "EnginePluginPackage");
        }

        /// <summary>
        /// Returns the Deploy Plugin child-operation output directory used for logs and reports for the blueprint-only
        /// branch. The packaged build itself now comes from the prepared project's staged-build directory.
        /// </summary>
        private static string GetBlueprintOperationOutputPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "BlueprintOnlyPackage");
        }

        /// <summary>
        /// Returns the Deploy Plugin child-operation output directory used for logs and reports for the demo branch. The
        /// packaged build itself now comes from the prepared project's staged-build directory.
        /// </summary>
        private static string GetDemoOperationOutputPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "DemoExe");
        }

        /// <summary>
        /// Returns the copied packaged blueprint build used only for launch validation so later demo packaging can mutate
        /// the shared staged-build output without racing the test launch.
        /// </summary>
        private static string GetBlueprintTestPackageSnapshotPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "BlueprintPackageTestSnapshot");
        }

        /// <summary>
        /// Returns a dedicated workspace copy used only for the example-project archive so archive cleanup never mutates
        /// the blueprint/demo variant used by packaging tasks.
        /// </summary>
        private static string GetExampleArchiveProjectPath(DeploymentWorkspaceState state)
        {
            return GetWorkspacePath(state.WorkspacePath, "ExampleProjectArchive");
        }

        /// <summary>
        /// Returns the engine marketplace install path used by the engine-plugin validation branches.
        /// </summary>
        private static string GetInstalledEnginePluginPath(DeploymentWorkspaceState state)
        {
            return Path.Combine(state.Engine.TargetPath, @"Engine\Plugins\Marketplace", state.SourcePlugin.Name);
        }

        /// <summary>
        /// Returns the packaged Windows build path from one prepared project's staged-build directory and validates that
        /// the expected executable tree exists before later launch or archive steps proceed.
        /// </summary>
        private static string GetRequiredPackagePath(Project project, DeploymentWorkspaceState state, string failureMessage)
        {
            string packagePath = project.GetStagedBuildWindowsPath(state.Engine);
            if (!PackagePaths.Instance.IsTargetDirectory(packagePath))
            {
                throw new InvalidOperationException($"{failureMessage}: {packagePath}");
            }

            return packagePath;
        }

        /// <summary>
        /// Creates one validated plugin target from a known workspace-relative plugin path.
        /// </summary>
        private static Plugin CreateRequiredPlugin(string pluginPath, string failureMessage)
        {
            if (!PluginPaths.Instance.IsTargetDirectory(pluginPath))
            {
                throw new InvalidOperationException($"{failureMessage}: {pluginPath}");
            }

            return new Plugin(pluginPath);
        }

        /// <summary>
        /// Creates one validated project target from a known workspace-relative project path.
        /// </summary>
        private static Project CreateRequiredProject(string projectPath, string failureMessage)
        {
            if (!ProjectPaths.Instance.IsTargetDirectory(projectPath))
            {
                throw new InvalidOperationException($"{failureMessage}: {projectPath}");
            }

            return new Project(projectPath);
        }

        /// <summary>
        /// Creates one validated packaged-build target from one prepared project's staged-build output.
        /// </summary>
        private static Package CreateRequiredPackage(Project project, DeploymentWorkspaceState state, string failureMessage)
        {
            return new Package(GetRequiredPackagePath(project, state, failureMessage));
        }

        /// <summary>
        /// Creates one validated packaged-build target from a known package directory path.
        /// </summary>
        private static Package CreateRequiredPackage(string packagePath, string failureMessage)
        {
            if (!PackagePaths.Instance.IsTargetDirectory(packagePath))
            {
                throw new InvalidOperationException($"{failureMessage}: {packagePath}");
            }

            return new Package(packagePath);
        }

        /// <summary>
        /// Clears one prepared project's staged-build output before a new package-only BuildCookRun pass so later
        /// package discovery cannot accidentally consume stale packaged files from an earlier deploy run.
        /// </summary>
        private static void DeleteExistingStagedPackageOutput(Project project, DeploymentWorkspaceState state)
        {
            string packagePath = project.GetStagedBuildWindowsPath(state.Engine);
            FileUtils.DeleteDirectoryIfExists(packagePath);
        }

        /// <summary>
        /// Returns one archive zip path beneath the operation output archive folder.
        /// </summary>
        private string GetArchiveZipPath(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, string archivePrefix, string archiveFileName)
        {
            return Path.Combine(GetOutputPath(operationParameters), "Archives", archivePrefix + archiveFileName);
        }

        /// <summary>
        /// Recreates one isolated prepared-project variant using the explicit materialization subset needed by later
        /// packaging and validation branches without copying every generated file from the shared example base.
        /// </summary>
        private static void CopyProjectVariant(string sourceProjectPath, string destinationProjectPath, ILogger logger, bool includeBuildOutputs = true)
        {
            FileUtils.DeleteDirectoryIfExists(destinationProjectPath);
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, "Prepared source project is not available for variant materialization");
            FileUtils.MaterializeDirectory(
                sourceProject.ProjectPath,
                destinationProjectPath,
                MaterializationSpecs.CreateProject(sourceProject, includePlugins: true, includeBuildOutputs: includeBuildOutputs),
                logger);
        }

        /// <summary>
        /// Describes the per-engine deployment subtree beneath the framework-owned root task.
        /// </summary>
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            AutomationOptions automationOptions = operationParameters.GetOptions<AutomationOptions>();
            PluginDeployOptions deployOptions = operationParameters.GetOptions<PluginDeployOptions>();

            /* The per-engine flow is authored as an explicit DAG so validation, packaging, and variant-preparation work can
               widen where the filesystem inputs are independent. Visible task dependencies still wait for each authored
               subtree to finish because joins target the visible task ids rather than hidden body-task ids. */
            root.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, steps =>
            {
                /* Workspace preparation stands on its own because later project-variant materialization only needs the
                   workspace copy, while plugin packaging owns every plugin-specific staging, build, and archive step that
                   fans out from that workspace. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareWorkspace = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder stagePlugin = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder pluginArtifactsFlow = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildPlugin = default!;
                prepareWorkspace = steps.Task("Prepare Workspace")
                    .Describe("Create the isolated engine-specific workspace from the prepared source")
                    .Run(PrepareStepAsync);

                pluginArtifactsFlow = steps.Task("Plugin Packaging")
                    .Describe("Stage, build, and archive the staged and distributable plugin artifacts used by later deploy branches")
                    .After(prepareWorkspace.Id);
                pluginArtifactsFlow.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, pluginArtifactScope =>
                {
                    stagePlugin = pluginArtifactScope.Task("Stage Plugin")
                        .Describe("Create the staged plugin copy used for packaging and archiving")
                        .Run(StagingStepAsync);

                    buildPlugin = pluginArtifactScope.Task("Build Distributable Plugin")
                        .Describe("Package the staged plugin into the distributable plugin payload used by later project and engine validation")
                        .After(stagePlugin.Id)
                        .Run(BuildPlugin);

                    pluginArtifactScope.Task("Archive Staged Plugin Source")
                        .Describe("Archive the staged source-style plugin payload as soon as the staging copy is ready")
                        .After(stagePlugin.Id)
                        .Run(ArchivePluginSourceAsync);

                    pluginArtifactScope.Task("Archive Distributable Plugin")
                        .Describe("Archive the packaged distributable plugin payload as soon as the built plugin output is ready")
                        .After(buildPlugin.Id)
                        .Run(ArchivePluginBuildAsync);
                });

                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareSharedBase = steps.Task("Prepare Shared Project-Plugin Base")
                    .Describe("Materialize, populate, and prebuild the shared code example base that later package branches clone or package directly")
                    .After(prepareWorkspace.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder materializeProjectPluginBase = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder installProjectPluginBase = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildExampleBase = default!;
                prepareSharedBase.Children(sharedBaseScope =>
                {
                    materializeProjectPluginBase = sharedBaseScope.Task("Materialize Project-Plugin Base")
                        .Describe("Copy the shared code example base from the workspace project before the built plugin is installed into it")
                        .Run(MaterializeProjectPluginBaseAsync);

                    installProjectPluginBase = sharedBaseScope.Task("Install Distributable Plugin Into Project-Plugin Base")
                        .Describe("Copy the built distributable plugin into the shared project-plugin base before downstream package variants clone it")
                        .After(materializeProjectPluginBase.Id, buildPlugin.Id)
                        .Run(InstallDistributablePluginIntoProjectPluginBaseAsync);

                    buildExampleBase = sharedBaseScope.Task("Prebuild Project-Plugin Base")
                        .Describe("Build the shared code example editor target once so downstream package variants can reuse editor outputs with -nocompileeditor")
                        .After(installProjectPluginBase.Id)
                        .Run(PrebuildProjectPluginBaseAsync);
                });

                /* The validation children all fan out from the shared prebuilt base, so the common dependency belongs on
                   the validation parent group instead of being repeated on each child task. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder validateSharedBase = steps.Task("Validate Shared Project-Plugin Base")
                    .Describe("Run optional launch validation branches against the shared prebuilt project-plugin base while later packaging preparation continues")
                    .After(prepareSharedBase.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testEditor = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testStandalone = default!;
                validateSharedBase.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, validationScope =>
                {
                    testEditor = validationScope.Task("Test Project-Plugin Base Editor")
                        .Describe("Launch and validate the prebuilt project-plugin base in the editor before downstream packaging completes")
                        .When(automationOptions.RunTests, "Run Tests is off.")
                        .Run(context => TestProjectPluginBaseEditorAsync(context, automationOptions));

                    testStandalone = validationScope.Task("Test Project-Plugin Base Standalone")
                        .Describe("Launch and validate the prebuilt project-plugin base in standalone mode before downstream packaging completes")
                        .When(automationOptions.RunTests && deployOptions.TestStandalone, automationOptions.RunTests ? "Test Standalone is off." : "Run Tests is off.")
                        .Run(context => TestProjectPluginBaseStandaloneAsync(context, automationOptions));
                });

                /* The Clang branch is its own optional sequential flow: first clone the shared base, then rebuild the
                   packaged plugin under Clang validation rules. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder clangValidationFlow = steps.Task("Clang Validation")
                    .Describe("Prepare the Clang validation variant and rebuild the distributable plugin payload under Clang")
                    .After(prepareSharedBase.Id)
                    .When(deployOptions.RunClangCompileCheck, "Run Clang Compile Check is off.");
                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareClangVariant = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder clangCheck = default!;
                clangValidationFlow.Children(clangScope =>
                {
                    prepareClangVariant = clangScope.Task("Prepare Clang Validation Variant")
                        .Describe("Clone the prebuilt project-plugin base for the optional Clang validation branch")
                        .Run(PrepareClangVariantAsync);

                    clangCheck = clangScope.Task("Run Clang Validation")
                        .Describe("Rebuild the packaged plugin in the Clang validation variant to verify the distributable plugin payload under Clang")
                        .After(prepareClangVariant.Id)
                        .Run(RunClangCompileCheck);
                });

                /* These variant clones are independent siblings with the same prerequisite, so the shared dependency is
                   authored once on the parent group and inherited by each sequentially independent child declaration. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder preparePackageVariants = steps.Task("Prepare Package Variants")
                    .Describe("Clone and mutate the shared prebuilt project-plugin base into the engine and blueprint packaging variants before the package flows begin")
                    .After(prepareSharedBase.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareEngineVariant = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareBlueprintDemoVariant = default!;
                preparePackageVariants.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, variantScope =>
                {
                    prepareEngineVariant = variantScope.Task("Prepare Engine-Plugin Variant")
                        .Describe("Clone the prebuilt project-plugin base and remove the project-level plugin so packaging resolves the built plugin from the engine install")
                        .Run(PrepareEnginePluginVariantAsync);

                    prepareBlueprintDemoVariant = variantScope.Task("Prepare Blueprint And Demo Variant")
                        .Describe("Clone the prebuilt project-plugin base, remove the project-level plugin, convert to blueprint-only, and prune plugins for blueprint and demo packaging")
                        .Run(PrepareBlueprintDemoVariantAsync);
                });

                /* The project-plugin packaging branch reuses the shared prebuilt base directly, but only once every clone
                   branch has finished copying that base. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageProjectPluginFlow = steps.Task("Project-Plugin Package")
                    .Describe("Build, package, and validate the example that keeps the built plugin installed at project level")
                    .After(prepareClangVariant.Id, preparePackageVariants.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildProjectPluginPackageTarget = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageProjectPlugin = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testProjectPlugin = default!;
                packageProjectPluginFlow.Children(flowScope =>
                {
                    buildProjectPluginPackageTarget = flowScope.Task("Build Project-Plugin Package Target")
                        .Describe("Build the shared project-plugin example target after all package variants have finished cloning the shared base")
                        .Run(BuildProjectPluginExampleTargetAsync);

                    packageProjectPlugin = flowScope.Task("Package Project-Plugin Example")
                        .Describe("Package the shared prebuilt code example with the built plugin still installed at project level after variant cloning finishes")
                        .After(buildProjectPluginPackageTarget.Id)
                        .Run(PackageProjectPluginExampleAsync);

                    testProjectPlugin = flowScope.Task("Test Project-Plugin Example")
                        .Describe("Launch and validate the packaged code example that keeps the built plugin installed at project level")
                        .After(packageProjectPlugin.Id)
                        .When(automationOptions.RunTests && deployOptions.TestPackageWithProjectPlugin, automationOptions.RunTests ? "Test Package With Project Plugin is off." : "Run Tests is off.")
                        .Run(context => TestProjectPluginExampleAsync(context, automationOptions));
                });

                /* Installing the built plugin into the engine is a shared handoff step for later engine-resolving
                   branches, not part of the project-plugin validation flow itself. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder installEnginePlugin = steps.Task("Install Built Plugin To Engine")
                    .Describe("Install the built plugin into the engine marketplace folder once the project-plugin example package is sealed")
                    .After(packageProjectPlugin.Id)
                    .Run(InstallBuiltPluginToEngineAsync);

                global::LocalAutomation.Runtime.ExecutionTaskBuilder enginePluginFlow = steps.Task("Engine-Plugin Package")
                    .Describe("Build, package, and validate the example that loads the built plugin from the engine install after its prepared variant is ready")
                    .After(prepareEngineVariant.Id, installEnginePlugin.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildEnginePluginPackageTarget = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageEnginePlugin = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testEnginePlugin = default!;
                enginePluginFlow.Children(flowScope =>
                {
                    buildEnginePluginPackageTarget = flowScope.Task("Build Engine-Plugin Package Target")
                        .Describe("Build the engine-plugin example target after the engine variant is prepared and the built plugin is installed into the engine")
                        .Run(BuildEnginePluginExampleTargetAsync);

                    packageEnginePlugin = flowScope.Task("Package Engine-Plugin Example")
                        .Describe("Package the code example variant that resolves the built plugin from the engine install")
                        .After(buildEnginePluginPackageTarget.Id)
                        .Run(PackageEnginePluginExampleAsync);

                    testEnginePlugin = flowScope.Task("Test Engine-Plugin Example")
                        .Describe("Launch and validate the packaged code example that resolves the built plugin from the engine install")
                        .After(packageEnginePlugin.Id)
                        .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                        .Run(context => TestEnginePluginExampleAsync(context, automationOptions));
                });

                /* The blueprint package is the shared prerequisite for launch validation and for demo packaging. The test
                   branch launches from a copied package snapshot so the later shipping package pass can safely recreate the
                   staged output in parallel. */
                global::LocalAutomation.Runtime.ExecutionTaskBuilder blueprintDemoFlow = steps.Task("Blueprint And Demo")
                    .Describe("Build, package, validate, and archive the blueprint and shipping demo outputs after the prepared variant is ready")
                    .After(prepareBlueprintDemoVariant.Id, installEnginePlugin.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildBlueprintPackageTarget = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageBlueprint = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder copyBlueprintPackageForTest = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder testBlueprint = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder demoPackageFlow = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildDemoTarget = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageDemo = default!;
                blueprintDemoFlow.Children(flowScope =>
                {
                    buildBlueprintPackageTarget = flowScope.Task("Build Blueprint-Only Package Target")
                        .Describe("Build the blueprint-only example target after the blueprint/demo variant is prepared and the built plugin is installed into the engine")
                        .Run(BuildBlueprintOnlyExampleTargetAsync);

                    packageBlueprint = flowScope.Task("Package Blueprint-Only Example")
                        .Describe("Package the blueprint-only example variant that resolves the built plugin from the engine install")
                        .After(buildBlueprintPackageTarget.Id)
                        .Run(PackageBlueprintOnlyExampleAsync);
                });

                /* The package task produces the shared staged output, so later blueprint launch validation and demo
                   packaging branches depend on that completed package step instead of being authored as static children
                   under the runnable package task itself. */
                copyBlueprintPackageForTest = steps.Task("Copy Blueprint Package For Test")
                    .Describe("Copy the packaged blueprint-only build to a dedicated snapshot so launch validation stays stable while demo packaging recreates the shared staged output")
                    .After(packageBlueprint.Id)
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Run(CopyBlueprintPackageForTestAsync);

                testBlueprint = steps.Task("Test Blueprint-Only Example")
                    .Describe("Launch and validate the packaged blueprint-only example that resolves the built plugin from the engine install")
                    .After(copyBlueprintPackageForTest.Id)
                    .When(automationOptions.RunTests && deployOptions.TestPackageWithEnginePlugin, automationOptions.RunTests ? "Test Package With Engine Plugin is off." : "Run Tests is off.")
                    .Run(context => TestBlueprintOnlyExampleAsync(context, automationOptions));

                demoPackageFlow = steps.Task("Demo Package")
                    .Describe("Build and package the shipping demo executable from the prepared blueprint/demo variant once the development blueprint package has been snapshotted for launch validation")
                    .After(packageBlueprint.Id);
                demoPackageFlow.Children(demoFlowScope =>
                {
                    buildDemoTarget = demoFlowScope.Task("Build Demo Executable Target")
                        .Describe("Build the shipping demo target from the prepared blueprint/demo variant in parallel with blueprint launch validation")
                        .Run(BuildDemoExecutableTargetAsync);

                    packageDemo = demoFlowScope.Task("Package Demo Executable")
                        .Describe("Package the shipping demo executable from the prepared blueprint/demo variant after the demo target build and blueprint test snapshot are ready")
                        .After(copyBlueprintPackageForTest.Id)
                        .Run(PackageDemoExecutableAsync);
                });

                steps.Task("Archive Example Project Payload")
                    .Describe("Archive the example-project payload from a dedicated archive copy once the blueprint and demo variant is prepared")
                    .After(prepareBlueprintDemoVariant.Id)
                    .Run(ArchiveExampleProjectAsync);

                steps.Task("Archive Demo Executable")
                    .Describe("Archive the packaged demo executable as soon as the demo output exists")
                    .After(packageDemo.Id)
                    .Run(ArchiveDemoPackageAsync);

            });
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

        /// <summary>
        /// Runs the scheduler-backed prepare step.
        /// </summary>
        private async Task PrepareStepAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing engine workspace");
            global::LocalAutomation.Runtime.ValidatedOperationParameters validatedParameters = context.ValidatedOperationParameters;
            Engine engine = GetTargetEngineInstall(validatedParameters)
                ?? throw new Exception("Engine not specified");
            DeployPreparedSourceState preparedSource = context.GetData<DeployPreparedSourceState>();
            Plugin plugin = preparedSource.SourcePlugin;
            Project hostProject = preparedSource.HostProject;
            string workspacePath = GetEngineTempPath(context, engine);
            string workspaceProjectPath = GetWorkspacePath(workspacePath, "HostProject");
            string workspacePluginPath = Path.Combine(workspaceProjectPath, "Plugins", plugin.Name);
            DeploymentWorkspaceState workspaceState = new(engine, plugin, hostProject, workspacePath, workspaceProjectPath, workspacePluginPath);

            context.Logger.LogInformation($"Engine version: {engine.Version}");
            string archivePrefix = await BuildArchivePrefixAsync(workspaceState);
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

            context.Logger.LogInformation($"Copying host project to workspace: {workspaceProjectPath}");
            FileUtils.MaterializeDirectory(hostProject.ProjectPath, workspaceProjectPath, MaterializationSpecs.CreateProject(hostProject), context.Logger);

            string workspacePluginsPath = Path.Combine(workspaceProjectPath, "Plugins");
            Directory.CreateDirectory(workspacePluginsPath);
            context.Logger.LogInformation($"Materializing target plugin into workspace: {workspacePluginPath}");
            FileUtils.MaterializeDirectory(plugin.PluginPath, workspacePluginPath, MaterializationSpecs.CreatePlugin(plugin), context.Logger);
            context.Logger.LogInformation($"Finished copying host project to workspace: {workspaceProjectPath}");

            if (!Directory.Exists(workspacePluginsPath))
            {
                throw new DirectoryNotFoundException($"Workspace project copy is missing Plugins directory after copy: {workspacePluginsPath}");
            }

            context.Logger.LogInformation($"Workspace Plugins directory: {workspacePluginsPath}");

            using Project workspaceProject = CreateRequiredProject(workspaceProjectPath, "Workspace project copy is not valid after copy");
            using Plugin workspacePlugin = CreateRequiredPlugin(workspacePluginPath, "Could not find the target plugin inside the workspace project");
            context.Logger.LogInformation($"Resolved workspace plugin: {workspacePlugin.PluginPath}");

            // Merge plugins are copied into the isolated workspace so later staging never reads mutable source folders.
            PluginDeploymentFlattening.MaterializeMergePlugins(hostProject, workspaceProject, plugin.Name, context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>(), context.Logger);

            UpdateProjectDescriptorForArchive(workspaceState, workspaceProject);
            context.Logger.LogInformation("Updated workspace project descriptor for archive output");
            context.SetOperationData(workspaceState);
            context.Logger.LogInformation("Stored workspace state for later deployment branches");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Runs the scheduler-backed staging step.
        /// </summary>
        private async Task StagingStepAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing plugin staging copy");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string workspacePluginPath = GetWorkspacePluginPath(state);
            string stagingPluginPath = GetStagingPluginPath(state);
            context.Logger.LogInformation($"Engine version: {state.Engine.Version}");
            context.Logger.LogInformation($"Workspace plugin: {workspacePluginPath}");
            context.Logger.LogInformation($"Staging destination: {stagingPluginPath}");
            FileUtils.DeleteDirectoryIfExists(stagingPluginPath);

            using Plugin workspacePlugin = CreateRequiredPlugin(workspacePluginPath, "Workspace plugin is not available for staging");
            FileUtils.MaterializeDirectory(workspacePlugin.PluginPath, stagingPluginPath, MaterializationSpecs.CreatePlugin(workspacePlugin), context.Logger);
            context.Logger.LogInformation($"Copied plugin to staging destination: {stagingPluginPath}");

            using Plugin stagingPlugin = CreateRequiredPlugin(stagingPluginPath, "Staged plugin was not created successfully");
            UpdatePluginDescriptorForArchive(state, stagingPlugin);
            using Project workspaceProject = CreateRequiredProject(GetWorkspaceProjectPath(state), "Workspace project is not available for plugin flattening");
            PluginDeploymentFlattening.ApplyToStagedPlugin(stagingPlugin, workspaceProject, context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>(), context.Logger);
            context.Logger.LogInformation($"Updated plugin descriptor for staging: {stagingPlugin.PluginDescriptor.VersionName}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Builds the archive filename prefix for the active engine-specific execution scope.
        /// </summary>
        private async Task<string> BuildArchivePrefixAsync(DeploymentWorkspaceState state)
        {
            Plugin plugin = state.SourcePlugin;
            PluginDescriptor pluginDescriptor = plugin.PluginDescriptor;
            bool standardBranch = true;
            string branchName = await VersionControlUtils.GetBranchNameAsync(state.HostProject.ProjectPath);
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
        /// Launches the prebuilt project-plugin base in the editor so deploy validation exercises the built plugin payload
        /// rather than the intermediate workspace copy.
        /// </summary>
        private async Task TestProjectPluginBaseEditorAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing project-plugin base in editor");
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.TestProjectPluginBaseEditor")
                .SetTag("trigger", "StepTransition");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project projectPluginBase = CreateRequiredProject(GetExampleProjectBasePath(state), "Project-plugin base is not available for editor test");
            global::LocalAutomation.Runtime.OperationParameters launchEditorParams = CreateParameters();
            launchEditorParams.Target = projectPluginBase;
            launchEditorParams.OutputPathOverride = GetWorkspacePath(state.WorkspacePath, "ProjectPluginBaseEditorLaunchOutput");
            launchEditorParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            launchEditorParams.SetOptions(automationOptions);
            ApplyValidationLaunchFlags(launchEditorParams);

            await RunChildOperationAsync<LaunchProjectEditor>(launchEditorParams, context, required: true, failureMessage: "Failed to launch project-plugin base in editor", hideChildOperationRootInGraph: true);
            activity.SetTag("result", "Completed");
        }

        /// <summary>
        /// Launches the prebuilt project-plugin base in standalone mode so deploy validation exercises the built plugin
        /// payload rather than the intermediate workspace copy.
        /// </summary>
        private async Task TestProjectPluginBaseStandaloneAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing project-plugin base in standalone");
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.TestProjectPluginBaseStandalone")
                .SetTag("trigger", "StepTransition");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project projectPluginBase = CreateRequiredProject(GetExampleProjectBasePath(state), "Project-plugin base is not available for standalone test");
            global::LocalAutomation.Runtime.OperationParameters launchStandaloneParams = CreateParameters();
            launchStandaloneParams.Target = projectPluginBase;
            launchStandaloneParams.OutputPathOverride = GetWorkspacePath(state.WorkspacePath, "ProjectPluginBaseStandaloneLaunchOutput");
            launchStandaloneParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            launchStandaloneParams.SetOptions(automationOptions);
            ApplyValidationLaunchFlags(launchStandaloneParams);

            await RunChildOperationAsync<LaunchStandalone>(launchStandaloneParams, context, required: true, failureMessage: "Failed to launch project-plugin base in standalone", hideChildOperationRootInGraph: true);
            activity.SetTag("result", "Completed");
        }

        // Package the staged plugin into a distributable output before deployment verification continues.
        private async Task BuildPlugin(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building plugin");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string stagingPluginPath = GetStagingPluginPath(state);
            string pluginBuildPath = GetBuiltPluginPath(state);
            FileUtils.DeleteDirectoryIfExists(pluginBuildPath);
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.BuildPlugin")
                .SetTag("plugin.name", state.SourcePlugin.Name)
                .SetTag("engine.version", state.Engine.Version.ToString())
                .SetTag("output.path", pluginBuildPath)
                .SetTag("trigger", "StepTransition");

            using Plugin stagingPlugin = CreateRequiredPlugin(stagingPluginPath, "Staged plugin is not available for packaging");
            global::LocalAutomation.Runtime.OperationParameters buildPluginParams = CreateParameters();
            buildPluginParams.Target = stagingPlugin;
            buildPluginParams.OutputPathOverride = pluginBuildPath;
            buildPluginParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            buildPluginParams.SetOptions(context.ValidatedOperationParameters.GetOptions<PluginBuildOptions>());

            using (PerformanceActivityScope childRunActivity = PerformanceTelemetry.StartActivity("DeployPlugin.BuildPlugin.RunChild"))
            {
                await RunChildOperationAsync<PackagePlugin>(buildPluginParams, context, required: true, failureMessage: "Plugin build failed", hideChildOperationRootInGraph: true);
                childRunActivity.SetTag("child.operation", nameof(PackagePlugin));
            }

            using (PerformanceActivityScope materializeActivity = PerformanceTelemetry.StartActivity("DeployPlugin.BuildPlugin.MaterializeBuiltPlugin"))
            {
                materializeActivity.SetTag("plugin.path", pluginBuildPath);
                using Plugin builtPlugin = CreateRequiredPlugin(pluginBuildPath, "Built plugin output is missing after packaging");
                context.Logger.LogInformation($"Validated built plugin output: {builtPlugin.PluginPath}");
            }

            context.Logger.LogInformation("Plugin build complete");
        }

        /// <summary>
        /// Materializes the shared project-plugin base from the workspace project before the built plugin is overlaid.
        /// Later packaging branches clone this prepared base instead of mutating one shared project directory in place.
        /// </summary>
        private async Task MaterializeProjectPluginBaseAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Materializing project-plugin base");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string exampleProjectPath = GetExampleProjectBasePath(state);
            FileUtils.DeleteDirectoryIfExists(exampleProjectPath);

            using Project workspaceProject = CreateRequiredProject(GetWorkspaceProjectPath(state), "Workspace project is not available for project-plugin base materialization");
            FileUtils.MaterializeDirectory(workspaceProject.ProjectPath, exampleProjectPath, MaterializationSpecs.CreateProject(workspaceProject), context.Logger);

            using Project exampleProject = CreateRequiredProject(exampleProjectPath, "Project-plugin base was not materialized successfully");
            UpdateProjectDescriptorForArchive(state, exampleProject);
            context.Logger.LogInformation($"Updated project descriptor for archive: EngineAssociation = {state.Engine.Version}");
            string exampleProjectVersion = ProjectConfig.BuildVersionWithEnginePrefix(state.SourcePlugin.PluginDescriptor.VersionName, state.Engine.Version);
            exampleProject.SetProjectVersion(exampleProjectVersion, context.Logger);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Installs the built distributable plugin into the already materialized project-plugin base so downstream
        /// packaging and launch validation exercise the shipped plugin payload rather than the workspace copy.
        /// </summary>
        private async Task InstallDistributablePluginIntoProjectPluginBaseAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Installing distributable plugin into project-plugin base");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProject = CreateRequiredProject(GetExampleProjectBasePath(state), "Project-plugin base is not available for plugin installation");
            using Plugin builtPlugin = CreateRequiredPlugin(GetBuiltPluginPath(state), "Built plugin is not available for project-plugin base installation");
            exampleProject.AddPlugin(builtPlugin);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Builds the shared project-plugin base's editor target once so later packaging variants can reuse its editor
        /// binaries through `-nocompileeditor` without waiting on AutomationTool's singleton lock.
        /// </summary>
        private async Task PrebuildProjectPluginBaseAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Prebuilding project-plugin base");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProject = CreateRequiredProject(GetExampleProjectBasePath(state), "Project-plugin base is not available for prebuild");

            global::LocalAutomation.Runtime.OperationParameters buildExampleProjectParams = CreateParameters();
            buildExampleProjectParams.Target = exampleProject;
            buildExampleProjectParams.OutputPathOverride = GetWorkspacePath(state.WorkspacePath, "PrebuildProjectPluginBaseOutput");
            buildExampleProjectParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            await RunChildOperationAsync<BuildEditorTarget>(buildExampleProjectParams, context, required: true, failureMessage: "Failed to prebuild project-plugin base editor target", hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Creates one isolated Clang-validation variant from the prebuilt project-plugin base.
        /// </summary>
        private async Task PrepareClangVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing Clang validation variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = GetExampleProjectBasePath(state);
            string clangVariantPath = GetClangVariantPath(state);
            CopyProjectVariant(sourceProjectPath, clangVariantPath, context.Logger);
            using Project clangVariant = CreateRequiredProject(clangVariantPath, "Clang validation variant was not created successfully");
            context.Logger.LogInformation($"Prepared Clang validation variant: {clangVariant.ProjectPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates one isolated engine-plugin packaging variant by cloning the prebuilt project-plugin base and removing the
        /// project-level plugin copy before later packaging depends on the engine install.
        /// </summary>
        private async Task PrepareEnginePluginVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing engine-plugin variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = GetExampleProjectBasePath(state);
            string engineVariantPath = GetEnginePluginVariantPath(state);
            CopyProjectVariant(sourceProjectPath, engineVariantPath, context.Logger);

            using Project engineVariant = CreateRequiredProject(engineVariantPath, "Engine-plugin variant was not created successfully");
            engineVariant.RemovePlugin(state.SourcePlugin.Name);
            context.Logger.LogInformation($"Prepared engine-plugin variant: {engineVariant.ProjectPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates one isolated blueprint/demo variant by cloning the prebuilt project-plugin base, removing the project-level
        /// plugin copy, converting the project to blueprint-only, and pruning sibling plugins according to deploy options.
        /// </summary>
        private async Task PrepareBlueprintDemoVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing blueprint and demo variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = GetExampleProjectBasePath(state);
            string blueprintVariantPath = GetBlueprintDemoVariantPath(state);
            CopyProjectVariant(sourceProjectPath, blueprintVariantPath, context.Logger);

            using Project blueprintVariant = CreateRequiredProject(blueprintVariantPath, "Blueprint/demo variant was not created successfully");
            blueprintVariant.RemovePlugin(state.SourcePlugin.Name);
            blueprintVariant.ConvertToBlueprintOnly();
            PreparePluginsForProject(state, blueprintVariant, context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>());
            context.Logger.LogInformation($"Prepared blueprint/demo variant: {blueprintVariant.ProjectPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates one shared parameter bag for BuildCookRun child operations that package prepared example-project
        /// variants. Deploy-specific phase selection now lives on the transient BuildCookRun operation instances rather
        /// than on a deploy-only public operation type.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreateExampleProjectBuildCookRunParams(Project project, DeploymentWorkspaceState state, string outputPath)
        {
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = project;
            parameters.OutputPathOverride = outputPath;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
            return parameters;
        }

        /// <summary>
        /// Creates the launch parameters for a packaged validation build, including explicit Unreal launch flags.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreatePackageLaunchParams(Package package, Engine engine, AutomationOptions automationOptions, string outputPath)
        {
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = package;
            parameters.OutputPathOverride = outputPath;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            parameters.SetOptions(automationOptions);
            ApplyValidationLaunchFlags(parameters);
            return parameters;
        }

        /// <summary>
        /// Applies the Unreal launch flags required for Deploy Plugin validation child launches.
        /// </summary>
        private static void ApplyValidationLaunchFlags(global::LocalAutomation.Runtime.OperationParameters launchParameters)
        {
            // Deploy validation launches are controlled automation runs, so they disable Unreal message-bus transports.
            launchParameters.GetOptions<FlagOptions>().NoMessaging = true;
        }

        /// <summary>
        /// Creates one transient BuildCookRun child operation whose phase set is specific to the surrounding deploy flow
        /// without forcing that preset into the public operation catalog.
        /// </summary>
        private static ConfiguredBuildCookRunProjectOperation CreateBuildCookRunProjectOperation(string operationName, BuildCookRunProjectRequest request)
        {
            return new ConfiguredBuildCookRunProjectOperation(operationName, request);
        }

        /// <summary>
        /// Creates the explicit package-only BuildCookRun request used by Deploy Plugin prepared-project branches. Deploy
        /// Plugin always reads packaged outputs from staged builds now, so these requests never enable UAT archive mode.
        /// </summary>
        private static BuildCookRunProjectRequest CreatePreparedProjectPackageRequest(BuildConfiguration configuration, bool noDebugInfo = false)
        {
            return new BuildCookRunProjectRequest(
                BuildCookRunProjectPhases.Cook | BuildCookRunProjectPhases.Stage | BuildCookRunProjectPhases.Pak | BuildCookRunProjectPhases.Package,
                configuration: configuration,
                noDebugInfo: noDebugInfo);
        }

        /// <summary>
        /// Builds one prepared project's game target so the later package-only BuildCookRun step has the staged game
        /// receipt it expects in Binaries/Win64 even when the project descriptor itself no longer has modules.
        /// </summary>
        private Task RunPreparedProjectBuildAsync(Project project, DeploymentWorkspaceState state, string outputPath, BuildConfiguration configuration, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage)
        {
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateExampleProjectBuildCookRunParams(project, state, outputPath);
            parameters.GetOptions<BuildConfigurationOptions>().Configuration = configuration;

            /* Stage still validates the game's target receipt when any enabled plugin contributes code, so the split build
               step must always compile the game target explicitly before package-only BuildCookRun takes over. */
            return RunChildOperationAsync(
                new BuildProjectTarget(),
                parameters,
                context,
                required: true,
                failureMessage: failureMessage,
                hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Runs the package-only BuildCookRun pass for one prepared example-project variant after its explicit target-build
        /// step has already completed. The staged package output is cleared here so package discovery cannot consume stale
        /// files from an earlier deploy run.
        /// </summary>
        private Task RunPreparedProjectPackageAsync(Project project, DeploymentWorkspaceState state, string outputPath, BuildConfiguration configuration, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage, bool noDebugInfo = false)
        {
            FileUtils.DeleteDirectoryIfExists(outputPath);
            DeleteExistingStagedPackageOutput(project, state);
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateExampleProjectBuildCookRunParams(project, state, outputPath);
            parameters.GetOptions<BuildConfigurationOptions>().Configuration = configuration;

            return RunChildOperationAsync(
                CreateBuildCookRunProjectOperation(
                    configuration == BuildConfiguration.Shipping ? "Package Demo Project" : "Package Prepared Project",
                    CreatePreparedProjectPackageRequest(configuration, noDebugInfo: noDebugInfo)),
                parameters,
                context,
                required: true,
                failureMessage: failureMessage,
                hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Runs one package-launch child operation against the already packaged build output for a branch.
        /// </summary>
        private Task RunLaunchPackageAsync(Package package, Engine engine, AutomationOptions automationOptions, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage, string outputPath)
        {
            return RunChildOperationAsync<LaunchPackage>(CreatePackageLaunchParams(package, engine, automationOptions, outputPath), context, required: true, failureMessage: failureMessage, hideChildOperationRootInGraph: true);
        }

        // Rebuild the packaged plugin in-place with Clang so validation matches the project-plugin flow Fab uses.
        private async Task RunClangCompileCheck(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Running Clang compile check");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.RunClangCompileCheck")
                .SetTag("plugin.name", state.SourcePlugin.Name)
                .SetTag("engine.version", state.Engine.Version.ToString());
            string exampleProjectPluginPath = Path.Combine(GetClangVariantPath(state), "Plugins", state.SourcePlugin.Name);
            using Plugin exampleProjectPlugin = CreateRequiredPlugin(exampleProjectPluginPath, "Could not find packaged plugin inside the Clang validation variant");

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

            await RunChildOperationAsync<BuildPlugin>(clangBuildParams, context, required: true, failureMessage: "Clang compile check failed", hideChildOperationRootInGraph: true);
            activity.SetTag("child.result", "Succeeded");
        }

        /// <summary>
        /// Scheduler wrapper for building the shared prebuilt project-plugin base after every sibling branch has finished
        /// cloning that base for its own later package variant.
        /// </summary>
        private async Task BuildProjectPluginExampleTargetAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building project-plugin example target");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProjectBase = CreateRequiredProject(GetExampleProjectBasePath(state), "Project-plugin base is not available for project-plugin target build");
            await RunPreparedProjectBuildAsync(exampleProjectBase, state, GetProjectPluginOperationOutputPath(state), BuildConfiguration.Development, context, "Build project-plugin example target failed");
        }

        /// <summary>
        /// Scheduler wrapper for packaging the shared prebuilt project-plugin base with the plugin installed at project
        /// level once its explicit target-build step has finished.
        /// </summary>
        private async Task PackageProjectPluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging project-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProjectBase = CreateRequiredProject(GetExampleProjectBasePath(state), "Project-plugin base is not available for project-plugin packaging");
            await RunPreparedProjectPackageAsync(exampleProjectBase, state, GetProjectPluginOperationOutputPath(state), BuildConfiguration.Development, context, "Package project-plugin example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged project-plugin example.
        /// </summary>
        private async Task TestProjectPluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing project-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProjectBase = CreateRequiredProject(GetExampleProjectBasePath(state), "Project-plugin base is not available for launch validation");
            Package projectPluginPackage = CreateRequiredPackage(exampleProjectBase, state, "Project-plugin package output is not available for launch");
            await RunLaunchPackageAsync(projectPluginPackage, state.Engine, automationOptions, context, "Launch and test with project plugin failed", GetWorkspacePath(state.WorkspacePath, "ProjectPluginLaunchOutput"));
        }

        /// <summary>
        /// Scheduler wrapper for installing the built plugin into the engine marketplace folder.
        /// </summary>
        private async Task InstallBuiltPluginToEngineAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Installing built plugin to engine");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string enginePluginsMarketplacePluginPath = GetInstalledEnginePluginPath(state);
            context.Logger.LogInformation($"Copying plugin to {enginePluginsMarketplacePluginPath}");
            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);
            using Plugin builtPlugin = CreateRequiredPlugin(GetBuiltPluginPath(state), "Built plugin is not available for engine installation");
            FileUtils.CopyDirectory(builtPlugin.PluginPath, enginePluginsMarketplacePluginPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Scheduler wrapper for building the engine-plugin example target once the engine variant is ready and the built
        /// plugin has been installed into the engine.
        /// </summary>
        private async Task BuildEnginePluginExampleTargetAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building engine-plugin example target");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project engineVariant = CreateRequiredProject(GetEnginePluginVariantPath(state), "Engine-plugin variant is not available for target build");
            await RunPreparedProjectBuildAsync(engineVariant, state, GetEnginePluginOperationOutputPath(state), BuildConfiguration.Development, context, "Build engine-plugin example target failed");
        }

        /// <summary>
        /// Scheduler wrapper for packaging the engine-plugin example.
        /// </summary>
        private async Task PackageEnginePluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging engine-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project engineVariant = CreateRequiredProject(GetEnginePluginVariantPath(state), "Engine-plugin variant is not available for packaging");
            await RunPreparedProjectPackageAsync(engineVariant, state, GetEnginePluginOperationOutputPath(state), BuildConfiguration.Development, context, "Package engine-plugin example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged engine-plugin example.
        /// </summary>
        private async Task TestEnginePluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing engine-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project engineVariant = CreateRequiredProject(GetEnginePluginVariantPath(state), "Engine-plugin variant is not available for launch validation");
            Package enginePluginPackage = CreateRequiredPackage(engineVariant, state, "Engine-plugin package output is not available for launch");
            await RunLaunchPackageAsync(enginePluginPackage, state.Engine, automationOptions, context, "Launch and test engine-plugin example failed", GetWorkspacePath(state.WorkspacePath, "EnginePluginLaunchOutput"));
        }

        /// <summary>
        /// Scheduler wrapper for building the blueprint-only example target after the blueprint/demo variant is ready and
        /// the built plugin has been installed into the engine.
        /// </summary>
        private async Task BuildBlueprintOnlyExampleTargetAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building blueprint-only example target");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project blueprintVariant = CreateRequiredProject(GetBlueprintDemoVariantPath(state), "Blueprint/demo variant is not available for target build");
            await RunPreparedProjectBuildAsync(blueprintVariant, state, GetBlueprintOperationOutputPath(state), BuildConfiguration.Development, context, "Build blueprint-only example target failed");
        }

        /// <summary>
        /// Scheduler wrapper for packaging the blueprint-only example.
        /// </summary>
        private async Task PackageBlueprintOnlyExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging blueprint-only example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project blueprintVariant = CreateRequiredProject(GetBlueprintDemoVariantPath(state), "Blueprint/demo variant is not available for packaging");

            await RunPreparedProjectPackageAsync(blueprintVariant, state, GetBlueprintOperationOutputPath(state), BuildConfiguration.Development, context, "Package blueprint-only example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the copied packaged blueprint-only example snapshot.
        /// </summary>
        private async Task TestBlueprintOnlyExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing blueprint-only example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            Package package = CreateRequiredPackage(GetBlueprintTestPackageSnapshotPath(state), "Blueprint package test snapshot is not available for launch");
            await RunLaunchPackageAsync(package, state.Engine, automationOptions, context, "Launch and test blueprint-only example failed", GetWorkspacePath(state.WorkspacePath, "BlueprintLaunchOutput"));
        }

        /// <summary>
        /// Copies the packaged blueprint-only build to a dedicated launch snapshot before the demo package branch recreates
        /// the shared staged-build output for shipping packaging.
        /// </summary>
        private async Task CopyBlueprintPackageForTestAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Copying blueprint package for test");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project blueprintVariant = CreateRequiredProject(GetBlueprintDemoVariantPath(state), "Blueprint/demo variant is not available for package snapshot");
            Package blueprintPackage = CreateRequiredPackage(blueprintVariant, state, "Blueprint package output is not available for snapshot copy");
            string snapshotPath = GetBlueprintTestPackageSnapshotPath(state);

            FileUtils.DeleteDirectoryIfExists(snapshotPath);
            FileUtils.CopyDirectory(blueprintPackage.TargetPath, snapshotPath);

            Package snapshotPackage = CreateRequiredPackage(snapshotPath, "Blueprint package test snapshot was not created successfully");
            context.Logger.LogInformation($"Prepared blueprint package test snapshot: {snapshotPackage.TargetPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Builds the shipping demo target from the prepared blueprint/demo variant once the development blueprint package
        /// has been snapshotted for launch validation.
        /// </summary>
        private async Task BuildDemoExecutableTargetAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Building demo executable target");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project demoVariant = CreateRequiredProject(GetBlueprintDemoVariantPath(state), "Blueprint/demo variant is not available for demo target build");
            await RunPreparedProjectBuildAsync(demoVariant, state, GetDemoOperationOutputPath(state), BuildConfiguration.Shipping, context, "Build demo project target for packaging failed");
        }

        /// <summary>
        /// Packages the shipping demo from the prepared blueprint/demo variant so the demo artifact no longer depends on a
        /// mutable shared project-plugin base instance.
        /// </summary>
        private async Task PackageDemoExecutableAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging demo executable");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string demoPackagePath = GetDemoOperationOutputPath(state);

            using Project demoVariant = CreateRequiredProject(GetBlueprintDemoVariantPath(state), "Blueprint/demo variant is not available for demo packaging");
            await RunPreparedProjectPackageAsync(demoVariant, state, demoPackagePath, BuildConfiguration.Shipping, context, "Package demo executable failed", noDebugInfo: true);
        }

        /// <summary>
        /// Prunes sibling plugins for archive and blueprint/demo preparation so the project variant only contains the
        /// plugins the deploy options allow and each retained plugin sheds generated intermediates.
        /// </summary>
        private void PreparePluginsForProject(DeploymentWorkspaceState state, Project targetProject, PluginDeployOptions deployOptions)
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

        /// <summary>
        /// Returns one archive zip path that must already exist before the final copy stage runs.
        /// </summary>
        private static string GetRequiredArchiveFile(string archiveZipPath, string failureMessage)
        {
            if (!File.Exists(archiveZipPath))
            {
                throw new FileNotFoundException(failureMessage, archiveZipPath);
            }

            return archiveZipPath;
        }

        /// <summary>
        /// Copies one produced archive zip to the configured archive output directory when the deploy options request an
        /// external archive location.
        /// </summary>
        private static void CopyArchiveToOutputIfConfigured(global::LocalAutomation.Runtime.ExecutionTaskContext context, string archiveZipPath)
        {
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            string archiveOutputPath = deployOptions.ArchivePath;
            if (string.IsNullOrEmpty(archiveOutputPath))
            {
                return;
            }

            context.Logger.LogInformation($"Copying archive to output path: {archiveOutputPath}");
            Directory.CreateDirectory(archiveOutputPath);
            if (!Directory.Exists(archiveOutputPath))
            {
                throw new Exception($"Could not resolve archive output: {archiveOutputPath}");
            }

            FileUtils.CopyFile(GetRequiredArchiveFile(archiveZipPath, $"Archive zip is missing: {Path.GetFileName(archiveZipPath)}"), archiveOutputPath, true, true);
        }

        /// <summary>
        /// Archives the staged plugin source payload as soon as the staging step has produced the archive-ready copy.
        /// </summary>
        private async Task ArchivePluginSourceAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving plugin source");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Plugin stagingPlugin = CreateRequiredPlugin(GetStagingPluginPath(state), "Staged plugin is not available for source archiving");
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");
            string pluginSourceArchiveZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "PluginSource.zip");

            Directory.CreateDirectory(archivePath);
            FileUtils.DeleteFileIfExists(pluginSourceArchiveZipPath);
            FileUtils.CreateZipFromDirectory(stagingPlugin.PluginPath, pluginSourceArchiveZipPath, true, context.Logger);
            CopyArchiveToOutputIfConfigured(context, pluginSourceArchiveZipPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Archives the packaged plugin build as soon as the packaged plugin output exists.
        /// </summary>
        private async Task ArchivePluginBuildAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving plugin build");
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            if (!deployOptions.ArchivePluginBuild)
            {
                await Task.CompletedTask;
                return;
            }

            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Plugin builtPlugin = CreateRequiredPlugin(GetBuiltPluginPath(state), "Built plugin is not available for archiving");
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");
            string pluginBuildZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "PluginBuild.zip");

            Directory.CreateDirectory(archivePath);
            FileUtils.DeleteFileIfExists(pluginBuildZipPath);
            FileUtils.CreateZipFromDirectory(builtPlugin.PluginPath, pluginBuildZipPath, true, context.Logger);
            CopyArchiveToOutputIfConfigured(context, pluginBuildZipPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Archives the example-project payload from a dedicated archive copy so archive pruning never mutates the live
        /// blueprint/demo variant used by package tasks.
        /// </summary>
        private async Task ArchiveExampleProjectAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving example project");
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            if (!deployOptions.ArchiveExampleProject)
            {
                await Task.CompletedTask;
                return;
            }

            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string archiveProjectPath = GetExampleArchiveProjectPath(state);
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");
            string exampleProjectZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "ExampleProject.zip");

            Directory.CreateDirectory(archivePath);
            CopyProjectVariant(GetBlueprintDemoVariantPath(state), archiveProjectPath, context.Logger, includeBuildOutputs: false);

            using Project archiveProject = CreateRequiredProject(archiveProjectPath, "Example-project archive copy is not available");
            string[] allowedExampleProjectSubDirectoryNames = { "Content", "Config", "Plugins" };
            FileUtils.DeleteOtherSubdirectories(archiveProject.ProjectPath, allowedExampleProjectSubDirectoryNames);
            FileUtils.DeleteFilesWithExtension(archiveProject.ProjectPath, new[] { ".pdb" }, SearchOption.AllDirectories);
            FileUtils.DeleteFileIfExists(exampleProjectZipPath);
            FileUtils.CreateZipFromDirectory(archiveProject.ProjectPath, exampleProjectZipPath, false, context.Logger);
            CopyArchiveToOutputIfConfigured(context, exampleProjectZipPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Archives the demo package as soon as the packaged demo output exists.
        /// </summary>
        private async Task ArchiveDemoPackageAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Archiving demo package");
            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            if (!deployOptions.ArchiveDemoPackage)
            {
                await Task.CompletedTask;
                return;
            }

            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project demoVariant = CreateRequiredProject(GetBlueprintDemoVariantPath(state), "Blueprint/demo variant is not available for demo archive validation");
            Package demoPackage = CreateRequiredPackage(demoVariant, state, "Demo package output is not available for archiving");
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");
            string demoPackageZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "DemoPackage.zip");

            Directory.CreateDirectory(archivePath);
            FileUtils.DeleteFileIfExists(demoPackageZipPath);
            FileUtils.CreateZipFromDirectory(demoPackage.TargetPath, demoPackageZipPath, false, context.Logger);
            CopyArchiveToOutputIfConfigured(context, demoPackageZipPath);
            await Task.CompletedTask;
        }
    }

    [Operation(SortOrder = 10)]
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
                string? firstLine;
                using (StreamReader reader = new(file))
                {
                    firstLine = reader.ReadLine();
                }

                if (firstLine == expectedComment)
                {
                    continue;
                }

                List<string> lines = File.ReadAllLines(file).ToList();
                if (firstLine != null && firstLine.StartsWith("//", StringComparison.Ordinal))
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
            context.SetOperationData(new DeployPreparedSourceState(plugin, hostProject));
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
