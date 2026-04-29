using System;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.BaseOperations;
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
        public static ExecutionTaskBuilder AddProjectBuild(
            ExecutionTaskScopeBuilder scope,
            string title,
            Operation buildOperation,
            Func<IOperationParameterContext, OperationParameters> createSourceParameters)
        {
            Operation resolvedBuildOperation = buildOperation ?? throw new ArgumentNullException(nameof(buildOperation));
            _ = createSourceParameters ?? throw new ArgumentNullException(nameof(createSourceParameters));

            // The generic cached-task core owns the prepare/run/copy task shape; this adapter only supplies Unreal cache
            // identity, workspace creation, and wrapped build parameters.
            return CachedWorkspaceTasks.Add<UnrealBuildWorkspaceCache.ProjectBuildWorkspace>(
                scope,
                title,
                workspace => UnrealExecutionLocks.GetBuildWorkspaceCacheLock(workspace.CachePath),
                resolvedBuildOperation,
                createSourceParameters,
                sourceParameters => CreateProjectBuildWorkspace(resolvedBuildOperation, sourceParameters),
                (workspace, context) => workspace.PrepareAsync(context),
                (workspace, sourceParameters) => CreateProjectBuildParameters(workspace, sourceParameters, resolvedBuildOperation),
                (workspace, context) => workspace.CopyOutputsAsync(context));
        }

        /// <summary>
        /// Adds a cached BuildPlugin package task that preserves UAT's host-project cache while copying only the packaged
        /// plugin payload back to the deployment workspace.
        /// </summary>
        public static ExecutionTaskBuilder AddPluginPackage(
            ExecutionTaskScopeBuilder scope,
            string title,
            string operationName,
            string role,
            Func<IOperationParameterContext, OperationParameters> createSourceParameters)
        {
            _ = createSourceParameters ?? throw new ArgumentNullException(nameof(createSourceParameters));
            PackagePlugin packageOperation = new();

            // PackagePlugin needs a fresh runtime parameter bag because UAT writes the package payload directly into the
            // prepared cache root.
            return CachedWorkspaceTasks.Add<UnrealBuildWorkspaceCache.PluginPackageWorkspace>(
                scope,
                title,
                workspace => UnrealExecutionLocks.GetBuildWorkspaceCacheLock(workspace.CachePath),
                packageOperation,
                createSourceParameters,
                sourceParameters => CreatePluginPackageWorkspace(packageOperation, sourceParameters, operationName, role),
                (workspace, context) => workspace.PrepareAsync(context),
                (workspace, sourceParameters) => CreatePluginPackageParameters(workspace, sourceParameters, packageOperation),
                (workspace, context) => workspace.CopyOutputsAsync(context));
        }

        /// <summary>
        /// Creates the cached project workspace from the same source parameters that describe the operation's source target.
        /// </summary>
        private static UnrealBuildWorkspaceCache.ProjectBuildWorkspace CreateProjectBuildWorkspace(Operation buildOperation, OperationParameters sourceParameters)
        {
            _ = sourceParameters ?? throw new ArgumentNullException(nameof(sourceParameters));
            BuildConfigurationOptions buildOptions = sourceParameters.GetOptions<BuildConfigurationOptions>();
            UbtCompilerOptions compilerOptions = sourceParameters.GetOptions<UbtCompilerOptions>();
            return UnrealBuildWorkspaceCache.CreateProjectBuildWorkspace(
                ResolveEngine(buildOperation, sourceParameters),
                GetSourceProjectPath(sourceParameters),
                buildOptions.Configuration,
                compilerOptions.Compiler,
                compilerOptions.CppStandard);
        }

        /// <summary>
        /// Creates the cached BuildPlugin workspace from the staged plugin target and destination in the source parameters.
        /// </summary>
        private static UnrealBuildWorkspaceCache.PluginPackageWorkspace CreatePluginPackageWorkspace(Operation packageOperation, OperationParameters sourceParameters, string operationName, string role)
        {
            _ = sourceParameters ?? throw new ArgumentNullException(nameof(sourceParameters));
            Plugin stagingPlugin = GetRequiredTarget<Plugin>(sourceParameters, packageOperation.OperationName);
            string destinationPluginPath = string.IsNullOrWhiteSpace(sourceParameters.OutputPathOverride)
                ? throw new InvalidOperationException($"Cached operation '{packageOperation.OperationName}' requires OutputPathOverride to identify the copied plugin destination.")
                : sourceParameters.OutputPathOverride;
            return UnrealBuildWorkspaceCache.CreatePluginPackageWorkspace(
                ResolveEngine(packageOperation, sourceParameters),
                operationName,
                role,
                stagingPlugin.PluginPath,
                destinationPluginPath,
                sourceParameters.GetOptions<PluginBuildOptions>());
        }

        /// <summary>
        /// Creates runtime parameters for a cached project build by cloning the source parameters and retargeting them to the cache.
        /// </summary>
        private static OperationParameters CreateProjectBuildParameters(
            UnrealBuildWorkspaceCache.ProjectBuildWorkspace workspace,
            OperationParameters baseParameters,
            Operation buildOperation)
        {
            _ = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _ = baseParameters ?? throw new ArgumentNullException(nameof(baseParameters));
            _ = buildOperation ?? throw new ArgumentNullException(nameof(buildOperation));
            Project cachedProject = workspace.OpenCachedProject();
            OperationParameters? buildParameters = null;
            try
            {
                buildParameters = buildOperation.CreateParameters(baseParameters);
                buildParameters.Target = CreateCachedTarget(baseParameters, cachedProject, buildOperation.OperationName);
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
                if (buildParameters != null && !ReferenceEquals(buildParameters.Target, cachedProject) && buildParameters.Target is IDisposable disposableTarget)
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
            UnrealBuildWorkspaceCache.PluginPackageWorkspace workspace,
            OperationParameters baseParameters,
            Operation packageOperation)
        {
            _ = workspace ?? throw new ArgumentNullException(nameof(workspace));
            _ = baseParameters ?? throw new ArgumentNullException(nameof(baseParameters));
            _ = packageOperation ?? throw new ArgumentNullException(nameof(packageOperation));
            Plugin stagingPlugin = workspace.OpenStagingPlugin();
            try
            {
                OperationParameters parameters = packageOperation.CreateParameters(baseParameters);
                parameters.Target = stagingPlugin;
                parameters.OutputPathOverride = workspace.CachePath;
                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { workspace.EngineVersion };
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

        /// <summary>
        /// Resolves the Unreal engine from the same parameters the wrapped operation will later execute with.
        /// </summary>
        private static Engine ResolveEngine(Operation operation, OperationParameters parameters)
        {
            if (operation is not UnrealOperation unrealOperation)
            {
                throw new InvalidOperationException($"Cached Unreal build operation '{operation.OperationName}' must derive from UnrealOperation.");
            }

            return unrealOperation.GetTargetEngineInstall(new ValidatedOperationParameters(operation, parameters))
                ?? throw new InvalidOperationException($"Cached Unreal build operation '{operation.OperationName}' could not resolve an engine from its parameters.");
        }

        /// <summary>
        /// Returns the project directory that supplies source inputs for a cached direct project build.
        /// </summary>
        private static string GetSourceProjectPath(OperationParameters sourceParameters)
        {
            if (sourceParameters.Target is Project sourceProject)
            {
                return sourceProject.ProjectPath;
            }

            if (sourceParameters.Target is Plugin sourcePlugin)
            {
                Project hostProject = sourcePlugin.GetHostProjectForDiagnostics();
                if (hostProject.IsValid)
                {
                    return hostProject.ProjectPath;
                }
            }

            throw new InvalidOperationException("Cached project builds require a Project target or a Plugin target inside a valid host project.");
        }

        /// <summary>
        /// Retargets cloned source parameters to the matching cached target type.
        /// </summary>
        private static IOperationTarget CreateCachedTarget(OperationParameters sourceParameters, Project cachedProject, string operationName)
        {
            if (sourceParameters.Target is Project)
            {
                return cachedProject;
            }

            if (sourceParameters.Target is Plugin sourcePlugin)
            {
                string cachedPluginPath = System.IO.Path.Combine(cachedProject.PluginsPath, sourcePlugin.Name);
                Plugin cachedPlugin = new(cachedPluginPath);
                if (!cachedPlugin.IsValid)
                {
                    cachedPlugin.Dispose();
                    throw new InvalidOperationException($"Cached operation '{operationName}' could not find plugin '{sourcePlugin.Name}' in cached project '{cachedProject.ProjectPath}'.");
                }

                return cachedPlugin;
            }

            throw new InvalidOperationException($"Cached operation '{operationName}' cannot retarget source target '{sourceParameters.Target?.TypeName ?? "<null>"}'.");
        }

        /// <summary>
        /// Reads the target from the parameter bag and verifies it has the expected runtime type.
        /// </summary>
        private static TTarget GetRequiredTarget<TTarget>(OperationParameters parameters, string operationName) where TTarget : IOperationTarget
        {
            return parameters.Target is TTarget target
                ? target
                : throw new InvalidOperationException($"Cached operation '{operationName}' requires a {typeof(TTarget).Name} source target.");
        }
    }
}
