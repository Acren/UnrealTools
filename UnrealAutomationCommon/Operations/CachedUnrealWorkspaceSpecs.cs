using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core.IO;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Builds declarative cached workspace specs for Unreal cache identities and creates runtime parameters for Unreal targets.
    /// </summary>
    internal static class CachedUnrealWorkspaceSpecs
    {
        /// <summary>
        /// Names UAT's generated BuildPlugin host project that must stay in the package cache but stay out of payload copies.
        /// </summary>
        private const string BuildPluginHostProjectDirectoryName = "HostProject";

        /// <summary>
        /// Keeps UAT's generated BuildPlugin host project available so package runs can reuse its intermediates.
        /// </summary>
        private const string PreserveHostProjectArgument = "-NoDeleteHostProject";

        /// <summary>
        /// Names project cache directories that are refreshed or regenerated for each direct build run.
        /// </summary>
        private static readonly string[] ProjectRefreshDirectories = { "Binaries", "Build", "Saved", "DerivedDataCache", "Config", "Source", "Content", "Plugins" };

        /// <summary>
        /// Names top-level project files that are refreshed before source inputs are materialized into a cache workspace.
        /// </summary>
        private static readonly string[] ProjectRefreshFilePatterns = { "*.uproject", "*.png" };

        /// <summary>
        /// Creates a declarative spec for refreshing a project into a cached direct-build workspace and copying outputs back.
        /// </summary>
        public static CachedWorkspaceSpec CreateProjectBuildSpec(
            Engine engine,
            string operationName,
            string role,
            string subjectName,
            string sourceProjectPath,
            BuildConfiguration configuration,
            UbtCompiler compiler,
            UbtCppStandard cppStandard)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            string resolvedOperationName = RequireText(operationName, nameof(operationName), "Operation name is required for a cached project build.");
            string resolvedRole = RequireText(role, nameof(role), "Role is required for a cached project build.");
            string resolvedSubjectName = RequireText(subjectName, nameof(subjectName), "Subject name is required for a cached project build.");
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, $"Cached build source project is not available for role '{resolvedRole}'");
            IReadOnlySet<string> projectPluginNames = MaterializationSpecs.GetProjectPluginNames(sourceProject);
            FileMaterializationSpec projectInputs = MaterializationSpecs.CreateProject(sourceProject, projectPluginNames);
            FileMaterializationSpec buildOutputs = MaterializationSpecs.CreateProjectBuildOutputs(sourceProject);
            string sourceProjectDirectoryPath = sourceProject.ProjectPath;
            string cacheKey = UnrealBuildWorkspaceCache.CreateProjectCacheKey(
                engine,
                resolvedOperationName,
                resolvedRole,
                resolvedSubjectName,
                sourceProject.Name,
                configuration,
                compiler,
                cppStandard,
                projectPluginNames.Select(pluginName => $"ProjectPlugin:{pluginName}"));
            string cachedProjectPath = UnrealBuildWorkspaceCache.GetProjectWorkspacePath(cacheKey);

            return new CachedWorkspaceSpec(
                cachedProjectPath,
                context => RefreshProjectBuildWorkspaceAsync(context, sourceProjectDirectoryPath, cachedProjectPath, projectInputs),
                context => CopyProjectBuildOutputsAsync(context, cachedProjectPath, sourceProjectDirectoryPath, buildOutputs));
        }

        /// <summary>
        /// Refreshes a session project into a cached direct-build workspace while preserving reusable Intermediate state.
        /// </summary>
        private static async Task RefreshProjectBuildWorkspaceAsync(ExecutionTaskContext context, string sourceProjectPath, string cachedProjectPath, FileMaterializationSpec projectInputs)
        {
            Directory.CreateDirectory(cachedProjectPath);
            DeleteRelativeDirectories(cachedProjectPath, ProjectRefreshDirectories, context.Logger);
            DeleteTopLevelFiles(cachedProjectPath, ProjectRefreshFilePatterns, context.Logger);
            context.Logger.LogInformation("Refreshing Unreal build cache workspace from '{SourceProjectPath}' to '{CachedProjectPath}'.", sourceProjectPath, cachedProjectPath);
            FileUtils.MaterializeDirectory(sourceProjectPath, cachedProjectPath, projectInputs, context.Logger, context.CancellationToken);

            if (ProjectPaths.Instance.IsTargetDirectory(cachedProjectPath))
            {
                await Task.CompletedTask;
                return;
            }

            context.Logger.LogWarning("Cached Unreal build workspace '{CachedProjectPath}' was invalid after refresh; recreating it without preserved intermediates.", cachedProjectPath);
            FileUtils.DeleteDirectoryIfExists(cachedProjectPath);
            Directory.CreateDirectory(cachedProjectPath);
            FileUtils.MaterializeDirectory(sourceProjectPath, cachedProjectPath, projectInputs, context.Logger, context.CancellationToken);
            if (!ProjectPaths.Instance.IsTargetDirectory(cachedProjectPath))
            {
                throw new InvalidOperationException($"Cached Unreal build workspace is not a valid project after refresh: {cachedProjectPath}");
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Copies generated project and plugin build outputs from the cache workspace into the session project.
        /// </summary>
        private static async Task CopyProjectBuildOutputsAsync(ExecutionTaskContext context, string cachedProjectPath, string destinationProjectPath, FileMaterializationSpec buildOutputs)
        {
            using Project cachedProject = CreateRequiredProject(cachedProjectPath, "Cached build workspace is not available for output copy");
            context.Logger.LogInformation("Copying cached Unreal build outputs from '{CachedProjectPath}' to session project '{SessionProjectPath}'.", cachedProject.ProjectPath, destinationProjectPath);
            FileUtils.MaterializeDirectory(cachedProject.ProjectPath, destinationProjectPath, buildOutputs, context.Logger, context.CancellationToken);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates runtime parameters for a cached project build and owns the cached-project target lifetime on failures.
        /// </summary>
        public static OperationParameters CreateProjectBuildParameters(
            IOperationParameterContext context,
            CachedWorkspaceSpec workspaceSpec,
            string role,
            Operation buildOperation,
            Func<IOperationParameterContext, Project, OperationParameters> createBuildParameters)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = workspaceSpec ?? throw new ArgumentNullException(nameof(workspaceSpec));
            _ = buildOperation ?? throw new ArgumentNullException(nameof(buildOperation));
            _ = createBuildParameters ?? throw new ArgumentNullException(nameof(createBuildParameters));
            Project cachedProject = CreateRequiredProject(workspaceSpec.CachePath, $"Cached build workspace is not available for role '{role}'");
            OperationParameters? createdParameters = null;
            try
            {
                OperationParameters buildParameters = createBuildParameters(context, cachedProject)
                    ?? throw new InvalidOperationException($"Cached build operation '{buildOperation.OperationName}' did not create operation parameters.");
                createdParameters = buildParameters;
                ValidateRuntimeTarget(buildOperation, buildParameters);
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
        /// Creates a declarative spec for cached UAT BuildPlugin packaging and payload-only copy-back.
        /// </summary>
        public static CachedWorkspaceSpec CreatePluginPackageSpec(
            Engine engine,
            string operationName,
            string role,
            string stagingPluginPath,
            string destinationPluginPath,
            PluginBuildOptions pluginBuildOptions)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            string resolvedOperationName = RequireText(operationName, nameof(operationName), "Operation name is required for cached plugin packaging.");
            string resolvedRole = RequireText(role, nameof(role), "Role is required for cached plugin packaging.");
            _ = pluginBuildOptions ?? throw new ArgumentNullException(nameof(pluginBuildOptions));
            using Plugin stagingPlugin = CreateRequiredPlugin(stagingPluginPath, "Staged plugin is not available for cached packaging");
            string cachedPackagePath = GetPluginPackageCachePath(engine, resolvedOperationName, resolvedRole, stagingPlugin, pluginBuildOptions);
            FileMaterializationSpec payloadSpec = new();
            payloadSpec.AddRootDirectory(required: true, excludedRelativePaths: new[] { BuildPluginHostProjectDirectoryName });
            string stagingPluginName = stagingPlugin.Name;

            return new CachedWorkspaceSpec(
                cachedPackagePath,
                context => PreparePluginPackageWorkspaceAsync(context, cachedPackagePath, stagingPluginName),
                context => CopyPluginPackageOutputAsync(context, cachedPackagePath, destinationPluginPath, payloadSpec));
        }

        /// <summary>
        /// Clears stale package payload files while preserving UAT's generated HostProject intermediate tree.
        /// </summary>
        private static async Task PreparePluginPackageWorkspaceAsync(ExecutionTaskContext context, string cachedPackagePath, string pluginName)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Preparing cached plugin package workspace");
            Directory.CreateDirectory(cachedPackagePath);
            DeleteTopLevelFiles(cachedPackagePath, new[] { "*" }, context.Logger);
            DeleteTopLevelDirectoriesExcept(cachedPackagePath, new[] { BuildPluginHostProjectDirectoryName }, context.Logger);
            DeleteDirectoryIfExists(Path.Combine(cachedPackagePath, BuildPluginHostProjectDirectoryName, "Plugins", pluginName), context.Logger);
            await Task.CompletedTask;
        }

        /// <summary>
        /// Recreates the session distributable plugin output from the fresh cached package payload.
        /// </summary>
        private static async Task CopyPluginPackageOutputAsync(ExecutionTaskContext context, string cachedPackagePath, string destinationPluginPath, FileMaterializationSpec payloadSpec)
        {
            using IDisposable nodeScope = context.Logger.BeginSection("Copying cached plugin package output");
            if (!Directory.Exists(cachedPackagePath))
            {
                throw new DirectoryNotFoundException($"Cached plugin package output does not exist: {cachedPackagePath}");
            }

            FileUtils.DeleteDirectoryIfExists(destinationPluginPath);
            context.Logger.LogInformation("Copying cached Unreal plugin package output from '{CachedPackagePath}' to '{DestinationPluginPath}'.", cachedPackagePath, destinationPluginPath);
            FileUtils.MaterializeDirectory(cachedPackagePath, destinationPluginPath, payloadSpec, context.Logger, context.CancellationToken);
            using Plugin builtPlugin = CreateRequiredPlugin(destinationPluginPath, "Built plugin output is missing after cached package copy-back");
            context.Logger.LogInformation($"Validated built plugin output: {builtPlugin.PluginPath}");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Creates PackagePlugin runtime parameters that direct UAT output into the prepared package cache root.
        /// </summary>
        public static OperationParameters CreatePluginPackageParameters(
            IOperationParameterContext context,
            CachedWorkspaceSpec workspaceSpec,
            Operation packageOperation,
            Engine engine,
            string stagingPluginPath,
            PluginBuildOptions pluginBuildOptions)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = workspaceSpec ?? throw new ArgumentNullException(nameof(workspaceSpec));
            _ = packageOperation ?? throw new ArgumentNullException(nameof(packageOperation));
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            _ = pluginBuildOptions ?? throw new ArgumentNullException(nameof(pluginBuildOptions));
            Plugin stagingPlugin = CreateRequiredPlugin(stagingPluginPath, "Staged plugin is not available for cached packaging");
            try
            {
                OperationParameters parameters = packageOperation.CreateParameters();
                parameters.Target = stagingPlugin;
                parameters.OutputPathOverride = workspaceSpec.CachePath;
                parameters.GetOptions<EngineVersionOptions>().EnabledVersions = new[] { engine.Version };
                parameters.SetOptions((PluginBuildOptions)pluginBuildOptions.Clone());
                parameters.GetOptions<AdditionalArgumentsOptions>().Arguments = PreserveHostProjectArgument;
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
        /// Builds a stable BuildPlugin cache path from engine identity, selected platforms, strict mode, and module shape.
        /// </summary>
        private static string GetPluginPackageCachePath(Engine engine, string operationName, string role, Plugin stagingPlugin, PluginBuildOptions pluginBuildOptions)
        {
            IEnumerable<string> targetPlatformShape = PluginBuildPlatformValidation.GetSelectedTargetPlatforms(pluginBuildOptions)
                .Select(platform => $"TargetPlatform:{platform}");
            IEnumerable<string> moduleShape = stagingPlugin.PluginDescriptor.Modules
                .Select(module => $"Module:{module.Name}:{module.Type}");
            string cacheKey = UnrealBuildWorkspaceCache.CreatePluginPackageCacheKey(
                engine,
                operationName,
                role,
                stagingPlugin.Name,
                targetPlatformShape
                    .Concat(moduleShape)
                    .Concat(new[] { $"StrictIncludes:{pluginBuildOptions.StrictIncludes}" }));

            return UnrealBuildWorkspaceCache.GetPluginPackageWorkspacePath(cacheKey);
        }

        /// <summary>
        /// Ensures a wrapped operation parameter factory selected a supported runtime target before returning it.
        /// </summary>
        private static void ValidateRuntimeTarget(Operation operation, OperationParameters parameters)
        {
            if (parameters.Target == null)
            {
                throw new InvalidOperationException($"Cached operation '{operation.OperationName}' did not select a target.");
            }

            if (!operation.SupportsTarget(parameters.Target))
            {
                throw new InvalidOperationException($"Cached operation '{operation.OperationName}' does not support runtime target '{parameters.Target.TypeName}'.");
            }
        }

        /// <summary>
        /// Opens a project path and fails loudly when the descriptor is missing or invalid.
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
        /// Creates one validated plugin target from a known package-cache or workspace path.
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
        /// Returns non-empty configuration text or throws with a parameter-specific message.
        /// </summary>
        private static string RequireText(string value, string parameterName, string message)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException(message, parameterName)
                : value;
        }

        /// <summary>
        /// Deletes relative child directories under a cache root when they exist.
        /// </summary>
        private static void DeleteRelativeDirectories(string rootPath, IEnumerable<string> relativeDirectories, ILogger logger)
        {
            foreach (string relativeDirectory in relativeDirectories)
            {
                DeleteDirectoryIfExists(Path.Combine(rootPath, relativeDirectory), logger);
            }
        }

        /// <summary>
        /// Deletes top-level files under a cache directory for each supplied search pattern.
        /// </summary>
        private static void DeleteTopLevelFiles(string path, IEnumerable<string> filePatterns, ILogger logger)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (string filePattern in filePatterns)
            {
                foreach (string filePath in Directory.GetFiles(path, filePattern, SearchOption.TopDirectoryOnly))
                {
                    logger.LogInformation("Deleting cached workspace file before refresh: {CachedFilePath}", filePath);
                    FileUtils.DeleteFileIfExists(filePath);
                }
            }
        }

        /// <summary>
        /// Deletes every top-level child directory except explicitly preserved directory names.
        /// </summary>
        private static void DeleteTopLevelDirectoriesExcept(string path, IEnumerable<string> preservedDirectoryNames, ILogger logger)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            HashSet<string> preservedNames = preservedDirectoryNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string directoryPath in Directory.GetDirectories(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (preservedNames.Contains(Path.GetFileName(directoryPath)))
                {
                    continue;
                }

                DeleteDirectoryIfExists(directoryPath, logger);
            }
        }

        /// <summary>
        /// Deletes a directory when present and logs the deletion for cache refresh diagnostics.
        /// </summary>
        private static void DeleteDirectoryIfExists(string path, ILogger logger)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            logger.LogInformation("Deleting cached workspace directory before refresh: {CachedDirectoryPath}", path);
            FileUtils.DeleteDirectoryIfExists(path);
        }
    }
}
