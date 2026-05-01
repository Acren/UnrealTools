using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
            public DeploymentWorkspaceState(Engine engine, Plugin sourcePlugin, Project hostProject, DeploymentWorkspaceLayout layout)
            {
                Engine = engine ?? throw new ArgumentNullException(nameof(engine));
                SourcePlugin = sourcePlugin ?? throw new ArgumentNullException(nameof(sourcePlugin));
                HostProject = hostProject ?? throw new ArgumentNullException(nameof(hostProject));
                Layout = layout ?? throw new ArgumentNullException(nameof(layout));
            }

            /// <summary>
            /// Gets the engine install used by this isolated deployment workspace.
            /// </summary>
            public Engine Engine { get; }

            /// <summary>
            /// Gets the source plugin being deployed into the per-engine workspace.
            /// </summary>
            public Plugin SourcePlugin { get; }

            /// <summary>
            /// Gets the source host project used to materialize the initial workspace project copy.
            /// </summary>
            public Project HostProject { get; }

            /// <summary>
            /// Gets every stable path role used by tasks in this per-engine deployment workspace.
            /// </summary>
            public DeploymentWorkspaceLayout Layout { get; }
        }

        /// <summary>
        /// Captures plugin staging decisions that later project materialization must respect.
        /// </summary>
        private sealed class DeploymentPluginStagingState
        {
            public DeploymentPluginStagingState(IReadOnlySet<string> mergePluginNames)
            {
                MergePluginNames = mergePluginNames ?? throw new ArgumentNullException(nameof(mergePluginNames));
            }

            /// <summary>
            /// Gets sibling plugins embedded into the staged plugin rather than carried as separate project plugins.
            /// </summary>
            public IReadOnlySet<string> MergePluginNames { get; }
        }

        /// <summary>
        /// Calculates stable session-output paths and persistent project-input roots for one engine-specific deployment.
        /// </summary>
        private sealed class DeploymentWorkspaceLayout
        {
            // The engine install root and plugin name are reused by several path roles inside this deployment layout.
            private readonly string _engineTargetPath;
            private readonly string _pluginName;

            public DeploymentWorkspaceLayout(
                Engine engine,
                string pluginName,
                global::LocalAutomation.Runtime.Workspace sessionWorkspace,
                Project workspaceProject,
                PluginBuildOptions pluginBuildOptions)
            {
                Engine resolvedEngine = engine ?? throw new ArgumentNullException(nameof(engine));
                _ = workspaceProject ?? throw new ArgumentNullException(nameof(workspaceProject));
                _ = pluginBuildOptions ?? throw new ArgumentNullException(nameof(pluginBuildOptions));
                _pluginName = string.IsNullOrWhiteSpace(pluginName)
                    ? throw new ArgumentException("Plugin name is required for deployment workspace paths.", nameof(pluginName))
                    : pluginName;
                _engineTargetPath = resolvedEngine.TargetPath;
                Workspace = sessionWorkspace ?? throw new ArgumentNullException(nameof(sessionWorkspace));

                // Persistent role workspaces are actual project/plugin input roots, so their identity is derived here with the layout.
                ExampleProjectBaseWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.ProjectRole(resolvedEngine, nameof(DeployPlugin), "ExampleProjectBase", workspaceProject));
                ClangVariantWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.ProjectRole(resolvedEngine, nameof(DeployPlugin), "ClangValidationVariant", workspaceProject));
                EnginePluginVariantWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.ProjectRole(resolvedEngine, nameof(DeployPlugin), "EnginePluginVariant", workspaceProject));
                BlueprintDemoVariantWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.ProjectRole(resolvedEngine, nameof(DeployPlugin), "BlueprintDemoVariant", workspaceProject));
                DistributablePluginPackageWorkspace = global::LocalAutomation.Runtime.Workspaces.Persistent(UnrealWorkspaceKeys.PluginPackage(resolvedEngine, nameof(DeployPlugin), "DistributablePluginPackage", _pluginName, pluginBuildOptions));
            }

            /// <summary>
            /// Gets the session-scoped workspace that holds run-specific artifacts and disposable source copies.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace Workspace { get; }

            /// <summary>
            /// Gets the persistent project root used for the project-plugin build and package branch.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace ExampleProjectBaseWorkspace { get; }

            /// <summary>
            /// Gets the persistent project root used for the optional Clang validation branch.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace ClangVariantWorkspace { get; }

            /// <summary>
            /// Gets the persistent project root used for the engine-plugin package branch.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace EnginePluginVariantWorkspace { get; }

            /// <summary>
            /// Gets the persistent project root shared by blueprint-only and shipping demo branches.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace BlueprintDemoVariantWorkspace { get; }

            /// <summary>
            /// Gets the persistent BuildPlugin-style host project root used for distributable plugin packaging.
            /// </summary>
            public global::LocalAutomation.Runtime.Workspace DistributablePluginPackageWorkspace { get; }

            public string RootPath => Workspace.RootPath;

            // Session paths are deleted per run; persistent project roots are separate first-class build inputs.
            public string WorkspaceProjectPath => PathFor("HostProject");
            public string WorkspacePluginPath => Path.Combine(WorkspaceProjectPath, "Plugins", _pluginName);
            public string StagingPluginPath => PathFor("PluginStaging", _pluginName);
            public string BuiltPluginPath => PathFor("PluginBuild", _pluginName);
            public string ExampleProjectBasePath => ExampleProjectBaseWorkspace.RootPath;
            public string EnginePluginVariantPath => EnginePluginVariantWorkspace.RootPath;
            public string ClangVariantPath => ClangVariantWorkspace.RootPath;
            public string BlueprintDemoVariantPath => BlueprintDemoVariantWorkspace.RootPath;
            public string ProjectPluginOperationOutputPath => PathFor("ProjectPluginPackage");
            public string EnginePluginOperationOutputPath => PathFor("EnginePluginPackage");
            public string BlueprintOperationOutputPath => PathFor("BlueprintOnlyPackage");
            public string DemoOperationOutputPath => PathFor("DemoExe");
            public string BlueprintTestPackageSnapshotPath => PathFor("BlueprintPackageTestSnapshot");
            public string ExampleArchiveProjectPath => PathFor("ExampleProjectArchive");
            public string InstalledEnginePluginPath => Path.Combine(_engineTargetPath, @"Engine\Plugins\Marketplace", _pluginName);
            public string PrebuildProjectPluginBaseOutputPath => PathFor("PrebuildProjectPluginBaseOutput");
            public string ClangCheckOutputPath => PathFor("ClangCheckOutput");
            public string ProjectPluginBaseEditorLaunchOutputPath => PathFor("ProjectPluginBaseEditorLaunchOutput");
            public string ProjectPluginBaseStandaloneLaunchOutputPath => PathFor("ProjectPluginBaseStandaloneLaunchOutput");
            public string ProjectPluginBaseQueryTargetsOutputPath => PathFor("ProjectPluginBaseQueryTargetsOutput");
            public string ProjectPluginLaunchOutputPath => PathFor("ProjectPluginLaunchOutput");
            public string EnginePluginLaunchOutputPath => PathFor("EnginePluginLaunchOutput");
            public string BlueprintLaunchOutputPath => PathFor("BlueprintLaunchOutput");

            private string PathFor(string label)
            {
                return Workspace.GetPath(global::LocalAutomation.Runtime.ExecutionPathConventions.MakeCompactSegment(label));
            }

            private string PathFor(string label, string leafName)
            {
                return Path.Combine(PathFor(label), leafName);
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
        /// Returns the session-owned staging root that BuildCookRun should use for one prepared-project package pass.
        /// </summary>
        private static string GetSessionPackageStagingRootPath(string outputPath)
        {
            return Path.Combine(outputPath, "StagedBuilds");
        }

        /// <summary>
        /// Returns the platform-specific cooked-data directory for one prepared-project package pass.
        /// </summary>
        private static string GetSessionPackageCookOutputPath(string outputPath, DeploymentWorkspaceState state)
        {
            return Path.Combine(outputPath, "Cooked", state.Engine.GetWindowsPlatformName());
        }

        /// <summary>
        /// Returns the packaged Windows build path from one session-scoped staging root and validates that the expected
        /// executable tree exists before later launch or archive steps proceed.
        /// </summary>
        private static string GetRequiredSessionPackagePath(string outputPath, DeploymentWorkspaceState state, string failureMessage)
        {
            string packagePath = Path.Combine(GetSessionPackageStagingRootPath(outputPath), state.Engine.GetWindowsPlatformName());
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
        /// Creates one validated packaged-build target from one session-scoped prepared-project package output.
        /// </summary>
        private static Package CreateRequiredSessionPackage(string outputPath, DeploymentWorkspaceState state, string failureMessage)
        {
            return new Package(GetRequiredSessionPackagePath(outputPath, state, failureMessage));
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
        /// Resolves the sibling project plugins that should remain separate project plugins during deploy validation.
        /// </summary>
        private static IReadOnlySet<string> GetIncludedSiblingPluginNames(
            Project referenceProject,
            string targetPluginName,
            PluginDeployOptions deployOptions,
            IReadOnlySet<string>? additionallyExcludedPluginNames = null)
        {
            HashSet<string> excludedPluginNames = GetExcludedPluginNames(deployOptions);
            if (additionallyExcludedPluginNames != null)
            {
                excludedPluginNames.UnionWith(additionallyExcludedPluginNames);
            }

            if (!deployOptions.IncludeOtherPlugins)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            List<Plugin> referencePlugins = referenceProject.Plugins;
            try
            {
                return referencePlugins
                    .Where(plugin => !plugin.Name.Equals(targetPluginName, StringComparison.OrdinalIgnoreCase))
                    .Where(plugin => !excludedPluginNames.Contains(plugin.Name))
                    .Select(plugin => plugin.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                // Project plugin enumeration creates watcher-backed plugin targets, so release them after reading names.
                foreach (Plugin plugin in referencePlugins)
                {
                    plugin.Dispose();
                }
            }
        }

        /// <summary>
        /// Parses the deploy option's comma-delimited sibling-plugin exclusion list into case-insensitive plugin names.
        /// </summary>
        private static HashSet<string> GetExcludedPluginNames(PluginDeployOptions deployOptions)
        {
            return deployOptions.ExcludePlugins
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Returns one archive zip path beneath the operation output archive folder.
        /// </summary>
        private string GetArchiveZipPath(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, string archivePrefix, string archiveFileName)
        {
            return Path.Combine(GetOutputPath(operationParameters), "Archives", archivePrefix + archiveFileName);
        }

        /// <summary>
        /// Describes the per-engine deployment subtree beneath the framework-owned root task.
        /// </summary>
        protected override void DescribeExecutionPlan(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, global::LocalAutomation.Runtime.ExecutionTaskBuilder root)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = GetTargetEngineInstall(operationParameters)
                ?? throw new InvalidOperationException("Deploy Plugin For Engine requires a resolved Unreal engine during plan authoring.");
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
                global::LocalAutomation.Runtime.ExecutionTaskBuilder packageDistributablePluginArtifact = default!;
                prepareWorkspace = steps.Task("Prepare Workspace")
                    .Describe("Create the isolated engine-specific workspace from the prepared source")
                    .Run(PrepareStepAsync);

                pluginArtifactsFlow = steps.Task("Plugin Packaging")
                    .Describe("Stage, package, and archive the plugin artifacts used by later deploy branches")
                    .After(prepareWorkspace.Id);
                pluginArtifactsFlow.Children(global::LocalAutomation.Runtime.ExecutionChildMode.Parallel, pluginArtifactScope =>
                {
                    stagePlugin = pluginArtifactScope.Task("Stage Plugin")
                        .Describe("Create the staged plugin copy and persistent BuildPlugin package input used for packaging and archiving")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.DistributablePluginPackageWorkspace.MutationLocks)
                        .Run(StagingStepAsync);

                    packageDistributablePluginArtifact = pluginArtifactScope.AddChildOperation(
                            new PackageDistributablePlugin(),
                            () => CreatePluginPackageAuthoringParameters(operationParameters),
                            context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                global::LocalAutomation.Runtime.Workspace workspace = state.Layout.DistributablePluginPackageWorkspace;
                                Plugin packagePlugin = CreateRequiredPlugin(workspace.GetPath("HostProject", "Plugins", state.SourcePlugin.Name), "Persistent host-project plugin is not available for packaging");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = packagePlugin;
                                parameters.OutputPathOverride = state.Layout.BuiltPluginPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                return parameters;
                            })
                        .After(stagePlugin.Id)
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.DistributablePluginPackageWorkspace.MutationLocks);

                    pluginArtifactScope.Task("Archive Staged Plugin Source")
                        .Describe("Archive the staged source-style plugin payload as soon as the staging copy is ready")
                        .After(stagePlugin.Id)
                        .Run(ArchivePluginSourceAsync);

                    pluginArtifactScope.Task("Archive Distributable Plugin")
                        .Describe("Archive the packaged distributable plugin payload as soon as the built plugin output is ready")
                        .After(packageDistributablePluginArtifact.Id)
                        .Run(ArchivePluginBuildAsync);
                });

                global::LocalAutomation.Runtime.ExecutionTaskBuilder prepareSharedBase = steps.Task("Prepare Shared Project-Plugin Base")
                    .Describe("Materialize, populate, and prebuild the shared code example base that later package branches clone or package directly")
                    .After(prepareWorkspace.Id, stagePlugin.Id);
                global::LocalAutomation.Runtime.ExecutionTaskBuilder materializeProjectPluginBase = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder installProjectPluginBase = default!;
                global::LocalAutomation.Runtime.ExecutionTaskBuilder buildExampleBase = default!;
                prepareSharedBase.Children(sharedBaseScope =>
                        {
                            materializeProjectPluginBase = sharedBaseScope.Task("Materialize Project-Plugin Base")
                                .Describe("Copy the shared code example base from the workspace project before the built plugin is installed into it")
                                .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                                .Run(MaterializeProjectPluginBaseAsync);

                            installProjectPluginBase = sharedBaseScope.Task("Install Distributable Plugin Into Project-Plugin Base")
                                    .Describe("Copy the built distributable plugin into the shared project-plugin base before downstream package variants clone it")
                                    .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                                    .After(materializeProjectPluginBase.Id, packageDistributablePluginArtifact.Id)
                                    .Run(InstallDistributablePluginIntoProjectPluginBaseAsync);

                            buildExampleBase = sharedBaseScope.AddChildOperation(
                                    "Prebuild Project-Plugin Base",
                                    new BuildEditorTarget(),
                                    () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Development),
                                    "Build the shared code example editor target that later package branches can reuse",
                                    context =>
                                    {
                                        DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                        Project project = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for editor target prebuild");
                                        global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                        parameters.Target = project;
                                        parameters.OutputPathOverride = state.Layout.PrebuildProjectPluginBaseOutputPath;
                                        parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                        parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                        return parameters;
                                    })
                                .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks);
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
                    global::LocalAutomation.Runtime.ExecutionTaskBuilder queryTargets = validationScope.Task("Query Project-Plugin Base Targets")
                        .Describe("Generate Unreal target metadata before the editor validation launch so editor startup can reuse the target cache")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                        .When(automationOptions.RunTests, "Run Tests is off.")
                        .Run(QueryProjectPluginBaseTargetsAsync);

                    testEditor = validationScope.Task("Test Project-Plugin Base Editor")
                        .Describe("Launch and validate the prebuilt project-plugin base in the editor after target metadata is ready")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                        .After(queryTargets.Id)
                        .When(automationOptions.RunTests, "Run Tests is off.")
                        .Run(context => LaunchProjectPluginBaseEditorAsync(context, automationOptions));

                    testStandalone = validationScope.Task("Test Project-Plugin Base Standalone")
                        .Describe("Launch and validate the prebuilt project-plugin base in standalone mode before downstream packaging completes")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
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
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ClangVariantWorkspace.MutationLocks)
                        .Run(PrepareClangVariantAsync);

                    clangCheck = clangScope.AddChildOperation(
                        "Run Clang Validation",
                        new BuildPlugin(),
                        () => CreatePluginBuildAuthoringParameters(operationParameters, UbtCompiler.Clang),
                        "Rebuild the packaged plugin in the Clang validation variant to verify the distributable plugin payload under Clang",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                using Project clangProject = CreateRequiredProject(state.Layout.ClangVariantPath, "Clang validation project is not available for plugin build");
                                Plugin clangPlugin = CreateRequiredPlugin(Path.Combine(clangProject.PluginsPath, state.SourcePlugin.Name), "Clang validation plugin is not available for build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = clangPlugin;
                                parameters.OutputPathOverride = state.Layout.ClangCheckOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                UbtCompilerOptions compilerOptions = parameters.GetOptions<UbtCompilerOptions>();
                                compilerOptions.Compiler = UbtCompiler.Clang;
                                compilerOptions.CppStandard = UbtCppStandard.Default;
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ClangVariantWorkspace.MutationLocks);
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
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.EnginePluginVariantWorkspace.MutationLocks)
                        .Run(PrepareEnginePluginVariantAsync);

                    prepareBlueprintDemoVariant = variantScope.Task("Prepare Blueprint And Demo Variant")
                        .Describe("Clone the prebuilt project-plugin base, remove the project-level plugin, convert to blueprint-only, and prune plugins for blueprint and demo packaging")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
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
                    buildProjectPluginPackageTarget = flowScope.AddChildOperation(
                        "Build Project-Plugin Package Target",
                        new BuildProjectTarget(),
                        () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Development, "-nocompileeditor"),
                        "Build the shared project-plugin example target after all package variants have finished cloning the shared base",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for package target build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = project;
                                parameters.OutputPathOverride = state.Layout.ProjectPluginOperationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks);

                    packageProjectPlugin = flowScope.Task("Package Project-Plugin Example")
                        .Describe("Package the shared prebuilt code example with the built plugin still installed at project level after variant cloning finishes")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
                        .After(buildProjectPluginPackageTarget.Id)
                        .Run(PackageProjectPluginExampleAsync);

                    testProjectPlugin = flowScope.Task("Test Project-Plugin Example")
                        .Describe("Launch and validate the packaged code example that keeps the built plugin installed at project level")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.ExampleProjectBaseWorkspace.MutationLocks)
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
                    buildEnginePluginPackageTarget = flowScope.AddChildOperation(
                        "Build Engine-Plugin Package Target",
                        new BuildProjectTarget(),
                        () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Development, "-nocompileeditor"),
                        "Build the engine-plugin example target after the engine variant is prepared and the built plugin is installed into the engine",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.EnginePluginVariantPath, "Engine-plugin variant is not available for package target build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = project;
                                parameters.OutputPathOverride = state.Layout.EnginePluginOperationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.EnginePluginVariantWorkspace.MutationLocks);

                    packageEnginePlugin = flowScope.Task("Package Engine-Plugin Example")
                        .Describe("Package the code example variant that resolves the built plugin from the engine install")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.EnginePluginVariantWorkspace.MutationLocks)
                        .After(buildEnginePluginPackageTarget.Id)
                        .Run(PackageEnginePluginExampleAsync);

                    testEnginePlugin = flowScope.Task("Test Engine-Plugin Example")
                        .Describe("Launch and validate the packaged code example that resolves the built plugin from the engine install")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.EnginePluginVariantWorkspace.MutationLocks)
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
                    buildBlueprintPackageTarget = flowScope.AddChildOperation(
                        "Build Blueprint-Only Package Target",
                        new BuildProjectTarget(),
                        () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Development, "-nocompileeditor"),
                        "Build the blueprint-only example target after the blueprint/demo variant is prepared and the built plugin is installed into the engine",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for package target build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = project;
                                parameters.OutputPathOverride = state.Layout.BlueprintOperationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
                                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks);

                    packageBlueprint = flowScope.Task("Package Blueprint-Only Example")
                        .Describe("Package the blueprint-only example variant that resolves the built plugin from the engine install")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
                        .After(buildBlueprintPackageTarget.Id)
                        .Run(PackageBlueprintOnlyExampleAsync);
                });

                /* The package task produces the shared staged output, so later blueprint launch validation and demo
                   packaging branches depend on that completed package step instead of being authored as static children
                   under the runnable package task itself. */
                copyBlueprintPackageForTest = steps.Task("Copy Blueprint Package For Test")
                    .Describe("Copy the packaged blueprint-only build to a dedicated snapshot so launch validation stays stable while demo packaging recreates the shared staged output")
                    .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
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
                    buildDemoTarget = demoFlowScope.AddChildOperation(
                        "Build Demo Executable Target",
                        new BuildProjectTarget(),
                        () => CreateProjectBuildAuthoringParameters(operationParameters, BuildConfiguration.Shipping, "-nocompileeditor"),
                        "Build the shipping demo target from the prepared blueprint/demo variant in parallel with blueprint launch validation",
                        context =>
                            {
                                DeploymentWorkspaceState state = context.GetData<DeploymentWorkspaceState>();
                                Project project = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for demo target build");
                                global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
                                parameters.Target = project;
                                parameters.OutputPathOverride = state.Layout.DemoOperationOutputPath;
                                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
                                parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Shipping;
                                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
                                return parameters;
                            })
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks);

                    packageDemo = demoFlowScope.Task("Package Demo Executable")
                        .Describe("Package the shipping demo executable from the prepared blueprint/demo variant after the demo target build and blueprint test snapshot are ready")
                        .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
                        .After(copyBlueprintPackageForTest.Id)
                        .Run(PackageDemoExecutableAsync);
                });

                steps.Task("Archive Example Project Payload")
                    .Describe("Archive the example-project payload from a dedicated archive copy once the blueprint and demo variant is prepared")
                    .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
                    .After(prepareBlueprintDemoVariant.Id)
                    .Run(ArchiveExampleProjectAsync);

                steps.Task("Archive Demo Executable")
                    .Describe("Archive the packaged demo executable as soon as the demo output exists")
                    .WithExecutionLocks(context => context.GetData<DeploymentWorkspaceState>().Layout.BlueprintDemoVariantWorkspace.MutationLocks)
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
            global::LocalAutomation.Runtime.Workspace sessionWorkspace = global::LocalAutomation.Runtime.Workspaces.Session(GetEngineTempPath(context, engine));
            string sessionRootPath = sessionWorkspace.RootPath;
            string workspaceProjectPath = sessionWorkspace.GetPath(global::LocalAutomation.Runtime.ExecutionPathConventions.MakeCompactSegment("HostProject"));
            string workspacePluginPath = Path.Combine(workspaceProjectPath, "Plugins", plugin.Name);

            context.Logger.LogInformation($"Engine version: {engine.Version}");
            context.Logger.LogInformation($"Source host project: {hostProject.ProjectPath}");
            context.Logger.LogInformation($"Source plugin: {plugin.PluginPath}");
            context.Logger.LogInformation($"Session workspace root: {sessionRootPath}");

            if (!Directory.Exists(hostProject.PluginsPath))
            {
                throw new DirectoryNotFoundException($"Host project is missing required Plugins directory: {hostProject.PluginsPath}");
            }

            context.Logger.LogInformation($"Source Plugins directory: {hostProject.PluginsPath}");

            context.Logger.LogInformation($"Deleting existing session workspace root: {sessionRootPath}");
            FileUtils.DeleteDirectoryIfExists(sessionRootPath);
            context.Logger.LogInformation($"Creating session workspace root: {sessionRootPath}");
            Directory.CreateDirectory(sessionRootPath);

            PluginDeployOptions deployOptions = context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>();
            PluginBuildOptions pluginBuildOptions = context.ValidatedOperationParameters.GetOptions<PluginBuildOptions>();
            IReadOnlySet<string> includedSiblingPluginNames = GetIncludedSiblingPluginNames(hostProject, plugin.Name, deployOptions);
            context.Logger.LogInformation($"Copying host project to workspace: {workspaceProjectPath}");
            FileUtils.MaterializeDirectory(hostProject.ProjectPath, workspaceProjectPath, MaterializationSpecs.CreateProject(hostProject, includedSiblingPluginNames), context.Logger, context.CancellationToken);

            string workspacePluginsPath = Path.Combine(workspaceProjectPath, "Plugins");
            Directory.CreateDirectory(workspacePluginsPath);
            context.Logger.LogInformation($"Materializing target plugin into workspace: {workspacePluginPath}");
            FileUtils.MaterializeDirectory(plugin.PluginPath, workspacePluginPath, MaterializationSpecs.CreatePlugin(plugin), context.Logger, context.CancellationToken);
            context.Logger.LogInformation($"Finished copying host project to workspace: {workspaceProjectPath}");

            if (!Directory.Exists(workspacePluginsPath))
            {
                throw new DirectoryNotFoundException($"Workspace project copy is missing Plugins directory after copy: {workspacePluginsPath}");
            }

            context.Logger.LogInformation($"Workspace Plugins directory: {workspacePluginsPath}");

            using Project workspaceProject = CreateRequiredProject(workspaceProjectPath, "Workspace project copy is not valid after copy");
            using Plugin workspacePlugin = CreateRequiredPlugin(workspacePluginPath, "Could not find the target plugin inside the workspace project");
            context.Logger.LogInformation($"Resolved workspace plugin: {workspacePlugin.PluginPath}");

            DeploymentWorkspaceLayout layout = new(engine, plugin.Name, sessionWorkspace, workspaceProject, pluginBuildOptions);
            DeploymentWorkspaceState workspaceState = new(engine, plugin, hostProject, layout);
            string archivePrefix = await BuildArchivePrefixAsync(workspaceState);
            context.Logger.LogInformation($"Archive name prefix is '{archivePrefix}'");
            context.Logger.LogInformation($"Project-plugin base root: {layout.ExampleProjectBasePath}");
            context.Logger.LogInformation($"Clang validation root: {layout.ClangVariantPath}");
            context.Logger.LogInformation($"Engine-plugin variant root: {layout.EnginePluginVariantPath}");
            context.Logger.LogInformation($"Blueprint/demo variant root: {layout.BlueprintDemoVariantPath}");

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
            string workspacePluginPath = state.Layout.WorkspacePluginPath;
            string stagingPluginPath = state.Layout.StagingPluginPath;
            context.Logger.LogInformation($"Engine version: {state.Engine.Version}");
            context.Logger.LogInformation($"Workspace plugin: {workspacePluginPath}");
            context.Logger.LogInformation($"Staging destination: {stagingPluginPath}");
            FileUtils.DeleteDirectoryIfExists(stagingPluginPath);

            using Plugin workspacePlugin = CreateRequiredPlugin(workspacePluginPath, "Workspace plugin is not available for staging");
            FileUtils.MaterializeDirectory(workspacePlugin.PluginPath, stagingPluginPath, MaterializationSpecs.CreatePlugin(workspacePlugin), context.Logger, context.CancellationToken);
            context.Logger.LogInformation($"Copied plugin to staging destination: {stagingPluginPath}");

            using Plugin stagingPlugin = CreateRequiredPlugin(stagingPluginPath, "Staged plugin was not created successfully");
            UpdatePluginDescriptorForArchive(state, stagingPlugin);
            using Project workspaceProject = CreateRequiredProject(state.Layout.WorkspaceProjectPath, "Workspace project is not available for plugin flattening");
            state.Layout.DistributablePluginPackageWorkspace.EnsureReady(context.Logger);
            string packageInputPluginPath = state.Layout.DistributablePluginPackageWorkspace.GetPath("HostProject", "Plugins", stagingPlugin.Name);
            context.Logger.LogInformation("Refreshing BuildPlugin host plugin input from '{StagingPluginPath}' to '{PackageInputPluginPath}'.", stagingPlugin.PluginPath, packageInputPluginPath);
            IReadOnlySet<string> mergePluginNames = PluginDeploymentFlattening.StagePluginForDeployment(
                stagingPlugin,
                state.HostProject,
                workspaceProject,
                packageInputPluginPath,
                context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>(),
                context.Logger,
                context.CancellationToken);
            context.SetOperationData(new DeploymentPluginStagingState(mergePluginNames));
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
        private async Task LaunchProjectPluginBaseEditorAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Launching project-plugin base editor");
            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("DeployPlugin.LaunchProjectPluginBaseEditor")
                .SetTag("trigger", "StepTransition");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project projectPluginBase = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for editor test");
            global::LocalAutomation.Runtime.OperationParameters launchEditorParams = CreateParameters();
            launchEditorParams.Target = projectPluginBase;
            launchEditorParams.OutputPathOverride = state.Layout.ProjectPluginBaseEditorLaunchOutputPath;
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
            using Project projectPluginBase = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for standalone test");
            global::LocalAutomation.Runtime.OperationParameters launchStandaloneParams = CreateParameters();
            launchStandaloneParams.Target = projectPluginBase;
            launchStandaloneParams.OutputPathOverride = state.Layout.ProjectPluginBaseStandaloneLaunchOutputPath;
            launchStandaloneParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            launchStandaloneParams.SetOptions(automationOptions);
            ApplyValidationLaunchFlags(launchStandaloneParams);

            await RunChildOperationAsync<LaunchStandalone>(launchStandaloneParams, context, required: true, failureMessage: "Failed to launch project-plugin base in standalone", hideChildOperationRootInGraph: true);
            activity.SetTag("result", "Completed");
        }

        /// <summary>
        /// Materializes the shared project-plugin base from the workspace project before the built plugin is overlaid.
        /// Later packaging branches clone this prepared base instead of mutating one shared project directory in place.
        /// </summary>
        private async Task MaterializeProjectPluginBaseAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Materializing project-plugin base");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string exampleProjectPath = state.Layout.ExampleProjectBasePath;

            using Project workspaceProject = CreateRequiredProject(state.Layout.WorkspaceProjectPath, "Workspace project is not available for project-plugin base materialization");
            state.Layout.ExampleProjectBaseWorkspace.EnsureReady(context.Logger);
            DeploymentPluginStagingState stagingState = context.GetOperationData<DeploymentPluginStagingState>();
            IReadOnlySet<string> includedSiblingPluginNames = GetIncludedSiblingPluginNames(state.HostProject, state.SourcePlugin.Name, context.ValidatedOperationParameters.GetOptions<PluginDeployOptions>(), stagingState.MergePluginNames);
            FileUtils.MaterializeDirectory(workspaceProject.ProjectPath, exampleProjectPath, MaterializationSpecs.CreateProject(workspaceProject, includedSiblingPluginNames), context.Logger, context.CancellationToken, mirrorDirectories: true);

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
            using Project exampleProject = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for plugin installation");
            using Plugin builtPlugin = CreateRequiredPlugin(state.Layout.BuiltPluginPath, "Built plugin is not available for project-plugin base installation");
            string installedPluginPath = Path.Combine(exampleProject.PluginsPath, builtPlugin.Name);
            Directory.CreateDirectory(exampleProject.PluginsPath);
            FileUtils.MaterializeDirectory(builtPlugin.PluginPath, installedPluginPath, MaterializationSpecs.CreatePlugin(builtPlugin, includeBuildOutputs: true), context.Logger, context.CancellationToken, mirrorDirectories: true);
            using Plugin installedPlugin = CreateRequiredPlugin(installedPluginPath, "Built plugin was not installed into the project-plugin base successfully");
            context.Logger.LogInformation("Installed distributable plugin into project-plugin base: {InstalledPluginPath}", installedPlugin.PluginPath);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Generates the project target metadata cache that Unreal Editor consumes during startup before validation launches
        /// need to start the editor process.
        /// </summary>
        private async Task QueryProjectPluginBaseTargetsAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Querying project-plugin base targets");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProject = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for target query");

            // Use a fresh parameter bag so the raw UBT mode command receives only the explicit arguments required to
            // produce TargetInfo.json and does not inherit unrelated launch or package arguments from the parent flow.
            global::LocalAutomation.Runtime.OperationParameters queryTargetsParams = CreateParameters();
            queryTargetsParams.Target = exampleProject;
            queryTargetsParams.OutputPathOverride = state.Layout.ProjectPluginBaseQueryTargetsOutputPath;
            queryTargetsParams.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            queryTargetsParams.GetOptions<AdditionalArgumentsOptions>().Arguments = BuildProjectTargetQueryArguments(exampleProject).ToString();
            await RunChildOperationAsync(new BuildBatOperation<Project>(), queryTargetsParams, context, required: true, failureMessage: "Failed to query project-plugin base targets", hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Builds the same direct UBT target-info query that Unreal Editor runs during startup so validation launches can
        /// reuse a fresh cache instead of running the query invisibly inside editor initialization.
        /// </summary>
        private static Arguments BuildProjectTargetQueryArguments(Project project)
        {
            Arguments arguments = new();
            arguments.SetKeyValue("Mode", "QueryTargets");
            arguments.SetKeyPath("Project", project.UProjectPath);
            arguments.SetKeyPath("Output", Path.Combine(project.ProjectPath, "Intermediate", "TargetInfo.json"));
            arguments.SetFlag("IncludeAllTargets");
            arguments.SetFlag("DontIncludeParentAssembly");
            return arguments;
        }

        /// <summary>
        /// Creates one isolated Clang-validation variant from the prebuilt project-plugin base while preserving the
        /// variant workspace's own root project build-output cache.
        /// </summary>
        private async Task PrepareClangVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing Clang validation variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = state.Layout.ExampleProjectBasePath;
            string clangVariantPath = state.Layout.ClangVariantPath;
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, "Project-plugin base is not available for Clang variant materialization");
            state.Layout.ClangVariantWorkspace.EnsureReady(context.Logger);
            FileUtils.MaterializeDirectory(sourceProject.ProjectPath, clangVariantPath, MaterializationSpecs.CreateProject(sourceProject, MaterializationSpecs.GetProjectPluginNames(sourceProject), includeProjectEditorBuildOutputs: true, includePluginBuildOutputs: true), context.Logger, context.CancellationToken, mirrorDirectories: true);
            using Project clangVariant = CreateRequiredProject(clangVariantPath, "Clang validation variant was not created successfully");
            context.Logger.LogInformation($"Prepared Clang validation variant: {clangVariant.ProjectPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates one isolated engine-plugin packaging variant by cloning the prebuilt project-plugin base, preserving the
        /// variant workspace's own root project build-output cache, and removing the project-level plugin copy before later
        /// packaging depends on the engine install.
        /// </summary>
        private async Task PrepareEnginePluginVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing engine-plugin variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = state.Layout.ExampleProjectBasePath;
            string engineVariantPath = state.Layout.EnginePluginVariantPath;
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, "Project-plugin base is not available for engine variant materialization");
            state.Layout.EnginePluginVariantWorkspace.EnsureReady(context.Logger);
            FileUtils.MaterializeDirectory(sourceProject.ProjectPath, engineVariantPath, MaterializationSpecs.CreateProject(sourceProject, MaterializationSpecs.GetProjectPluginNames(sourceProject), includeProjectEditorBuildOutputs: true, includePluginBuildOutputs: true), context.Logger, context.CancellationToken, mirrorDirectories: true);

            using Project engineVariant = CreateRequiredProject(engineVariantPath, "Engine-plugin variant was not created successfully");
            engineVariant.RemovePlugin(state.SourcePlugin.Name);
            context.Logger.LogInformation($"Prepared engine-plugin variant: {engineVariant.ProjectPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates one isolated blueprint/demo variant by cloning the prebuilt project-plugin base, preserving the variant
        /// workspace's own root project build-output cache, removing the project-level plugin copy, and converting the
        /// project to blueprint-only while preserving the selected sibling plugin set.
        /// </summary>
        private async Task PrepareBlueprintDemoVariantAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing blueprint and demo variant");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string sourceProjectPath = state.Layout.ExampleProjectBasePath;
            string blueprintVariantPath = state.Layout.BlueprintDemoVariantPath;
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, "Project-plugin base is not available for blueprint/demo variant materialization");
            state.Layout.BlueprintDemoVariantWorkspace.EnsureReady(context.Logger);
            FileUtils.MaterializeDirectory(sourceProject.ProjectPath, blueprintVariantPath, MaterializationSpecs.CreateProject(sourceProject, MaterializationSpecs.GetProjectPluginNames(sourceProject), includeProjectEditorBuildOutputs: true, includePluginBuildOutputs: true), context.Logger, context.CancellationToken, mirrorDirectories: true);

            using Project blueprintVariant = CreateRequiredProject(blueprintVariantPath, "Blueprint/demo variant was not created successfully");
            blueprintVariant.RemovePlugin(state.SourcePlugin.Name);
            blueprintVariant.ConvertToBlueprintOnly();
            context.Logger.LogInformation($"Prepared blueprint/demo variant: {blueprintVariant.ProjectPath}");
            await Task.CompletedTask;
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
        /// Creates authoring-time package parameters from the source plugin so prepared package subtasks can be previewed.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreatePluginPackageAuthoringParameters(
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = plugin;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            return parameters;
        }

        /// <summary>
        /// Creates authoring-time project-build parameters from the source host project for static child-operation subtasks.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreateProjectBuildAuthoringParameters(
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters,
            BuildConfiguration configuration,
            string? additionalArguments = null)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = plugin.HostProject;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            parameters.GetOptions<BuildConfigurationOptions>().Configuration = configuration;
            if (!string.IsNullOrWhiteSpace(additionalArguments))
            {
                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = additionalArguments;
            }

            return parameters;
        }

        /// <summary>
        /// Creates authoring-time plugin-build parameters from the source plugin for static child-operation subtasks.
        /// </summary>
        private global::LocalAutomation.Runtime.OperationParameters CreatePluginBuildAuthoringParameters(
            global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters,
            UbtCompiler compiler)
        {
            Plugin plugin = GetRequiredTarget(operationParameters);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            global::LocalAutomation.Runtime.OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = plugin;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            parameters.GetOptions<BuildConfigurationOptions>().Configuration = BuildConfiguration.Development;
            UbtCompilerOptions compilerOptions = parameters.GetOptions<UbtCompilerOptions>();
            compilerOptions.Compiler = compiler;
            compilerOptions.CppStandard = UbtCppStandard.Default;
            return parameters;
        }

        /// <summary>
        /// Creates the explicit package-only BuildCookRun request used by Deploy Plugin prepared-project branches. Session
        /// staging and cook roots keep package artifacts out of persistent project workspaces while preserving build caches.
        /// </summary>
        private static BuildCookRunProjectRequest CreatePreparedProjectPackageRequest(BuildConfiguration configuration, string stagingDirectory, string cookOutputDirectory, bool noDebugInfo = false)
        {
            return new BuildCookRunProjectRequest(
                BuildCookRunProjectPhases.Cook | BuildCookRunProjectPhases.Stage | BuildCookRunProjectPhases.Pak | BuildCookRunProjectPhases.Package,
                configuration: configuration,
                noDebugInfo: noDebugInfo,
                stagingDirectory: stagingDirectory,
                cookOutputDirectory: cookOutputDirectory);
        }

        /// <summary>
        /// Runs the package-only BuildCookRun pass for one prepared example-project variant after its explicit target-build
        /// step has already completed. The staged package output is cleared here so package discovery cannot consume stale
        /// files from an earlier deploy run.
        /// </summary>
        private Task RunPreparedProjectPackageAsync(Project project, DeploymentWorkspaceState state, string outputPath, BuildConfiguration configuration, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage, bool noDebugInfo = false)
        {
            // Package and cook output are run-scoped, so clear the whole session package root before UAT writes into it.
            FileUtils.DeleteDirectoryIfExists(outputPath);
            DeletePersistentProjectPackageOutputs(project, context.Logger);

            // UAT appends the platform under -stagingdirectory but consumes -CookOutputDir as the final platform path.
            string stagingRootPath = GetSessionPackageStagingRootPath(outputPath);
            string cookOutputPath = GetSessionPackageCookOutputPath(outputPath, state);

            // The prepared project already has editor binaries, so package-only BuildCookRun skips editor compilation explicitly.
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = project;
            parameters.OutputPathOverride = outputPath;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { state.Engine.Version };
            parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = "-nocompileeditor";
            parameters.GetOptions<BuildConfigurationOptions>().Configuration = configuration;

            // The transient operation name keeps the execution graph readable without adding a public deploy-only operation type.
            string operationName = configuration == BuildConfiguration.Shipping ? "Package Demo Project" : "Package Prepared Project";

            return RunChildOperationAsync(
                new ConfiguredBuildCookRunProjectOperation(operationName, CreatePreparedProjectPackageRequest(configuration, stagingRootPath, cookOutputPath, noDebugInfo: noDebugInfo)),
                parameters,
                context,
                required: true,
                failureMessage: failureMessage,
                hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Removes package-only output roots from a persistent prepared project so warm build caches do not retain staged
        /// payloads or cooked content after those outputs have moved into the session workspace.
        /// </summary>
        private static void DeletePersistentProjectPackageOutputs(Project project, ILogger logger)
        {
            string savedPath = Path.Combine(project.ProjectPath, "Saved");
            FileUtils.DeleteDirectoryIfExists(Path.Combine(savedPath, "StagedBuilds"), logger);
            FileUtils.DeleteDirectoryIfExists(Path.Combine(savedPath, "Cooked"), logger);
        }

        /// <summary>
        /// Runs one package-launch child operation against the already packaged build output for a branch.
        /// </summary>
        private Task RunLaunchPackageAsync(Package package, Engine engine, AutomationOptions automationOptions, global::LocalAutomation.Runtime.ExecutionTaskContext context, string failureMessage, string outputPath)
        {
            // Launch parameters are created at runtime because each branch discovers its packaged executable after staging.
            global::LocalAutomation.Runtime.OperationParameters parameters = CreateParameters();
            parameters.Target = package;
            parameters.OutputPathOverride = outputPath;
            parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
            parameters.SetOptions(automationOptions);
            ApplyValidationLaunchFlags(parameters);
            return RunChildOperationAsync<LaunchPackage>(parameters, context, required: true, failureMessage: failureMessage, hideChildOperationRootInGraph: true);
        }

        /// <summary>
        /// Scheduler wrapper for packaging the shared prebuilt project-plugin base with the plugin installed at project
        /// level once its explicit target-build step has finished.
        /// </summary>
        private async Task PackageProjectPluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging project-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project exampleProjectBase = CreateRequiredProject(state.Layout.ExampleProjectBasePath, "Project-plugin base is not available for project-plugin packaging");
            await RunPreparedProjectPackageAsync(exampleProjectBase, state, state.Layout.ProjectPluginOperationOutputPath, BuildConfiguration.Development, context, "Package project-plugin example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged project-plugin example.
        /// </summary>
        private async Task TestProjectPluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing project-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            Package projectPluginPackage = CreateRequiredSessionPackage(state.Layout.ProjectPluginOperationOutputPath, state, "Project-plugin package output is not available for launch");
            await RunLaunchPackageAsync(projectPluginPackage, state.Engine, automationOptions, context, "Launch and test with project plugin failed", state.Layout.ProjectPluginLaunchOutputPath);
        }

        /// <summary>
        /// Scheduler wrapper for installing the built plugin into the engine marketplace folder.
        /// </summary>
        private async Task InstallBuiltPluginToEngineAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Installing built plugin to engine");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string enginePluginsMarketplacePluginPath = state.Layout.InstalledEnginePluginPath;
            context.Logger.LogInformation($"Copying plugin to {enginePluginsMarketplacePluginPath}");
            FileUtils.DeleteDirectoryIfExists(enginePluginsMarketplacePluginPath);
            using Plugin builtPlugin = CreateRequiredPlugin(state.Layout.BuiltPluginPath, "Built plugin is not available for engine installation");
            FileUtils.CopyDirectory(builtPlugin.PluginPath, enginePluginsMarketplacePluginPath, cancellationToken: context.CancellationToken);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Scheduler wrapper for packaging the engine-plugin example.
        /// </summary>
        private async Task PackageEnginePluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging engine-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project engineVariant = CreateRequiredProject(state.Layout.EnginePluginVariantPath, "Engine-plugin variant is not available for packaging");
            await RunPreparedProjectPackageAsync(engineVariant, state, state.Layout.EnginePluginOperationOutputPath, BuildConfiguration.Development, context, "Package engine-plugin example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the packaged engine-plugin example.
        /// </summary>
        private async Task TestEnginePluginExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing engine-plugin example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            Package enginePluginPackage = CreateRequiredSessionPackage(state.Layout.EnginePluginOperationOutputPath, state, "Engine-plugin package output is not available for launch");
            await RunLaunchPackageAsync(enginePluginPackage, state.Engine, automationOptions, context, "Launch and test engine-plugin example failed", state.Layout.EnginePluginLaunchOutputPath);
        }

        /// <summary>
        /// Scheduler wrapper for packaging the blueprint-only example.
        /// </summary>
        private async Task PackageBlueprintOnlyExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging blueprint-only example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            using Project blueprintVariant = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for packaging");

            await RunPreparedProjectPackageAsync(blueprintVariant, state, state.Layout.BlueprintOperationOutputPath, BuildConfiguration.Development, context, "Package blueprint-only example failed");
        }

        /// <summary>
        /// Scheduler wrapper for testing the copied packaged blueprint-only example snapshot.
        /// </summary>
        private async Task TestBlueprintOnlyExampleAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context, AutomationOptions automationOptions)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Testing blueprint-only example");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            Package package = CreateRequiredPackage(state.Layout.BlueprintTestPackageSnapshotPath, "Blueprint package test snapshot is not available for launch");
            await RunLaunchPackageAsync(package, state.Engine, automationOptions, context, "Launch and test blueprint-only example failed", state.Layout.BlueprintLaunchOutputPath);
        }

        /// <summary>
        /// Copies the packaged blueprint-only build to a dedicated launch snapshot before the demo package branch recreates
        /// the shared staged-build output for shipping packaging.
        /// </summary>
        private async Task CopyBlueprintPackageForTestAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Copying blueprint package for test");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            Package blueprintPackage = CreateRequiredSessionPackage(state.Layout.BlueprintOperationOutputPath, state, "Blueprint package output is not available for snapshot copy");
            string snapshotPath = state.Layout.BlueprintTestPackageSnapshotPath;

            FileUtils.DeleteDirectoryIfExists(snapshotPath);
            FileUtils.CopyDirectory(blueprintPackage.TargetPath, snapshotPath, cancellationToken: context.CancellationToken);

            Package snapshotPackage = CreateRequiredPackage(snapshotPath, "Blueprint package test snapshot was not created successfully");
            context.Logger.LogInformation($"Prepared blueprint package test snapshot: {snapshotPackage.TargetPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Packages the shipping demo from the prepared blueprint/demo variant so the demo artifact no longer depends on a
        /// mutable shared project-plugin base instance.
        /// </summary>
        private async Task PackageDemoExecutableAsync(global::LocalAutomation.Runtime.ExecutionTaskContext context)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Packaging demo executable");
            DeploymentWorkspaceState state = context.GetOperationData<DeploymentWorkspaceState>();
            string demoPackagePath = state.Layout.DemoOperationOutputPath;

            using Project demoVariant = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for demo packaging");
            await RunPreparedProjectPackageAsync(demoVariant, state, demoPackagePath, BuildConfiguration.Shipping, context, "Package demo executable failed", noDebugInfo: true);
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
            using Plugin stagingPlugin = CreateRequiredPlugin(state.Layout.StagingPluginPath, "Staged plugin is not available for source archiving");
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
            using Plugin builtPlugin = CreateRequiredPlugin(state.Layout.BuiltPluginPath, "Built plugin is not available for archiving");
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
            string archiveProjectPath = state.Layout.ExampleArchiveProjectPath;
            string archivePrefix = await BuildArchivePrefixAsync(state);
            string archivePath = Path.Combine(GetOutputPath(context.ValidatedOperationParameters), "Archives");
            string exampleProjectZipPath = GetArchiveZipPath(context.ValidatedOperationParameters, archivePrefix, "ExampleProject.zip");

            Directory.CreateDirectory(archivePath);
            FileUtils.DeleteDirectoryIfExists(archiveProjectPath, context.Logger);
            using Project blueprintVariant = CreateRequiredProject(state.Layout.BlueprintDemoVariantPath, "Blueprint/demo variant is not available for archive materialization");
            // Source example archives omit root project binaries but must keep each code plugin's packaged module outputs.
            FileMaterializationSpec archiveProjectSpec = MaterializationSpecs.CreateProject(blueprintVariant, MaterializationSpecs.GetProjectPluginNames(blueprintVariant), includePluginBuildOutputs: true);
            FileUtils.MaterializeDirectory(blueprintVariant.ProjectPath, archiveProjectPath, archiveProjectSpec, context.Logger, context.CancellationToken, mirrorDirectories: true);

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
            Package demoPackage = CreateRequiredSessionPackage(state.Layout.DemoOperationOutputPath, state, "Demo package output is not available for archiving");
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
                    engines.AddChildOperation<DeployPluginForEngine>(
                        $"UE {engineVersion.MajorMinorString}",
                        () =>
                        {
                            // Each authored child targets exactly one engine so nested Unreal operations inherit a single version.
                            global::LocalAutomation.Runtime.OperationParameters childParameters = operationParameters.CreateChild();
                            childParameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engineVersion };
                            return childParameters;
                        },
                        "Per-engine deployment scope");
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
