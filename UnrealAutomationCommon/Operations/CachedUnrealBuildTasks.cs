using System;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Operations.OperationTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Authors Unreal-specific cached build tasks while keeping operation plans focused on their dependency graph.
    /// </summary>
    internal static class CachedUnrealBuildTasks
    {
        /// <summary>
        /// Keeps UAT's generated BuildPlugin host project available so package runs can reuse its intermediates.
        /// </summary>
        private const string PreserveHostProjectArgument = "-NoDeleteHostProject";

        /// <summary>
        /// Adds a cached direct project-build task that refreshes a source project, runs the supplied build operation, and
        /// copies the generated build outputs back to the source project.
        /// </summary>
        public static ExecutionTaskBuilder AddProjectBuild<TState>(
            ExecutionTaskScopeBuilder scope,
            string title,
            Operation buildOperation,
            ValidatedOperationParameters operationParameters,
            Func<TState, Engine> getEngine,
            Func<TState, string> getSourceProjectPath,
            BuildConfiguration configuration,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters,
            UbtCompiler compiler = UbtCompiler.Default,
            UbtCppStandard cppStandard = UbtCppStandard.Default)
            where TState : class
        {
            _ = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
            Operation resolvedBuildOperation = buildOperation ?? throw new ArgumentNullException(nameof(buildOperation));
            _ = getEngine ?? throw new ArgumentNullException(nameof(getEngine));
            _ = getSourceProjectPath ?? throw new ArgumentNullException(nameof(getSourceProjectPath));
            _ = createBuildParameters ?? throw new ArgumentNullException(nameof(createBuildParameters));

            // The generic cached-task core owns the prepare/run/copy task shape; this adapter only supplies Unreal cache
            // identity, workspace creation, and wrapped build parameters.
            return CachedWorkspaceTasks.Add<UnrealBuildWorkspaceCache.ProjectBuildWorkspace>(
                scope,
                title,
                workspace => UnrealExecutionLocks.GetBuildWorkspaceCacheLock(workspace.CachePath),
                resolvedBuildOperation,
                operationParameters,
                context =>
                {
                    TState state = context.GetData<TState>();
                    return UnrealBuildWorkspaceCache.CreateProjectBuildWorkspace(
                        getEngine(state),
                        getSourceProjectPath(state),
                        configuration,
                        compiler,
                        cppStandard);
                },
                (workspace, context) => workspace.PrepareAsync(context),
                (context, workspace) => CreateProjectBuildParameters(context, workspace, resolvedBuildOperation, createBuildParameters),
                (workspace, context) => workspace.CopyOutputsAsync(context));
        }

        /// <summary>
        /// Adds a cached BuildPlugin package task that preserves UAT's host-project cache while copying only the packaged
        /// plugin payload back to the deployment workspace.
        /// </summary>
        public static ExecutionTaskBuilder AddPluginPackage<TState>(
            ExecutionTaskScopeBuilder scope,
            string title,
            string operationName,
            string role,
            ValidatedOperationParameters operationParameters,
            Func<TState, Engine> getEngine,
            Func<TState, string> getStagingPluginPath,
            Func<TState, string> getDestinationPluginPath)
            where TState : class
        {
            _ = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
            _ = getEngine ?? throw new ArgumentNullException(nameof(getEngine));
            _ = getStagingPluginPath ?? throw new ArgumentNullException(nameof(getStagingPluginPath));
            _ = getDestinationPluginPath ?? throw new ArgumentNullException(nameof(getDestinationPluginPath));
            PackagePlugin packageOperation = new();
            PluginBuildOptions pluginBuildOptions = operationParameters.GetOptions<PluginBuildOptions>();

            // PackagePlugin needs a fresh runtime parameter bag because UAT writes the package payload directly into the
            // prepared cache root.
            return CachedWorkspaceTasks.Add<UnrealBuildWorkspaceCache.PluginPackageWorkspace>(
                scope,
                title,
                workspace => UnrealExecutionLocks.GetBuildWorkspaceCacheLock(workspace.CachePath),
                packageOperation,
                operationParameters,
                context =>
                {
                    TState state = context.GetData<TState>();
                    return UnrealBuildWorkspaceCache.CreatePluginPackageWorkspace(
                        getEngine(state),
                        operationName,
                        role,
                        getStagingPluginPath(state),
                        getDestinationPluginPath(state),
                        pluginBuildOptions);
                },
                (workspace, context) => workspace.PrepareAsync(context),
                (context, workspace) => CreatePluginPackageParameters(context, workspace, packageOperation, pluginBuildOptions),
                (workspace, context) => workspace.CopyOutputsAsync(context));
        }

        /// <summary>
        /// Creates runtime parameters for a cached project build and owns the cached-project target lifetime on failures.
        /// </summary>
        private static OperationParameters CreateProjectBuildParameters(
            IOperationParameterContext context,
            UnrealBuildWorkspaceCache.ProjectBuildWorkspace workspace,
            Operation buildOperation,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _ = buildOperation ?? throw new ArgumentNullException(nameof(buildOperation));
            _ = createBuildParameters ?? throw new ArgumentNullException(nameof(createBuildParameters));
            Project cachedProject = workspace.OpenCachedProject();
            OperationParameters? createdParameters = null;
            try
            {
                OperationParameters buildParameters = createBuildParameters(context, cachedProject)
                    ?? throw new InvalidOperationException($"Cached build operation '{buildOperation.OperationName}' did not create operation parameters.");
                createdParameters = buildParameters;
                CachedWorkspaceTasks.ValidateRuntimeTarget(buildOperation, buildParameters);
                if (!ReferenceEquals(buildParameters.Target, cachedProject))
                {
                    cachedProject.Dispose();
                }

                return buildParameters;
            }
            catch
            {
                cachedProject.Dispose();
                if (createdParameters != null && !ReferenceEquals(createdParameters.Target, cachedProject) && createdParameters.Target is IDisposable disposableTarget)
                {
                    disposableTarget.Dispose();
                }

                throw;
            }
        }

        /// <summary>
        /// Creates PackagePlugin runtime parameters that direct UAT output into the prepared package cache root.
        /// </summary>
        private static OperationParameters CreatePluginPackageParameters(
            IOperationParameterContext context,
            UnrealBuildWorkspaceCache.PluginPackageWorkspace workspace,
            Operation packageOperation,
            PluginBuildOptions pluginBuildOptions)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _ = packageOperation ?? throw new ArgumentNullException(nameof(packageOperation));
            _ = pluginBuildOptions ?? throw new ArgumentNullException(nameof(pluginBuildOptions));
            Plugin stagingPlugin = workspace.OpenStagingPlugin();
            try
            {
                OperationParameters parameters = packageOperation.CreateParameters();
                parameters.Target = stagingPlugin;
                parameters.OutputPathOverride = workspace.CachePath;
                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { workspace.EngineVersion };
                parameters.SetOptions((PluginBuildOptions)pluginBuildOptions.Clone());
                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = PreserveHostProjectArgument;
                CachedWorkspaceTasks.ValidateRuntimeTarget(packageOperation, parameters);
                return parameters;
            }
            catch
            {
                // Runtime parameters own the staging-plugin target only after successful creation.
                stagingPlugin.Dispose();
                throw;
            }
        }
    }
}
