using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LocalAutomation.Core.IO;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Provides stable project build workspaces for temporary Unreal projects so UBT can reuse its Intermediate tree and
    /// shared PCH outputs across repeated automation runs.
    /// </summary>
    internal static class UnrealBuildWorkspaceCache
    {
        /// <summary>
        /// Keeps cache hash folders short while leaving enough entropy that unrelated build identities do not collide in
        /// the persistent Unreal build cache root.
        /// </summary>
        private const int CacheHashLength = 16;

        /// <summary>
        /// Names UAT's generated BuildPlugin host project that must stay in the package cache but stay out of payload copies.
        /// </summary>
        private const string BuildPluginHostProjectDirectoryName = "HostProject";

        /// <summary>
        /// Creates one cached direct-build workspace from the source project and compile-environment identity.
        /// </summary>
        internal static ProjectBuildWorkspace CreateProjectBuildWorkspace(
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
            string resolvedRole = RequireText(role, nameof(role), "Role is required for a cached project build.");
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, $"Cached build source project is not available for role '{resolvedRole}'");
            IReadOnlySet<string> projectPluginRelativePaths = MaterializationSpecs.GetProjectPluginRelativePaths(sourceProject);
            IReadOnlySet<string> projectPluginModuleShape = MaterializationSpecs.GetProjectPluginModuleShape(sourceProject);
            IReadOnlySet<string> projectPluginNames = projectPluginRelativePaths
                .Select(Path.GetFileName)
                .Where(pluginName => !string.IsNullOrWhiteSpace(pluginName))
                .Select(pluginName => pluginName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            FileMaterializationSpec projectInputs = MaterializationSpecs.CreateProject(sourceProject, projectPluginNames);
            FileMaterializationSpec buildOutputs = MaterializationSpecs.CreateProjectBuildOutputs(sourceProject);
            string cachePath = GetProjectBuildCachePath(engine, operationName, resolvedRole, subjectName, sourceProject, configuration, compiler, cppStandard, projectPluginRelativePaths.Concat(projectPluginModuleShape));
            return new ProjectBuildWorkspace(cachePath, sourceProject.ProjectPath, projectInputs, buildOutputs, projectPluginRelativePaths, resolvedRole);
        }

        /// <summary>
        /// Creates one cached BuildPlugin package workspace that can preserve UAT host-project intermediates between runs.
        /// </summary>
        internal static PluginPackageWorkspace CreatePluginPackageWorkspace(
            Engine engine,
            string operationName,
            string role,
            string stagingPluginPath,
            string destinationPluginPath,
            PluginBuildOptions pluginBuildOptions)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            string resolvedRole = RequireText(role, nameof(role), "Role is required for cached plugin packaging.");
            _ = pluginBuildOptions ?? throw new ArgumentNullException(nameof(pluginBuildOptions));
            using Plugin stagingPlugin = CreateRequiredPlugin(stagingPluginPath, "Staged plugin is not available for cached packaging");
            string cachePath = GetPluginPackageCachePath(engine, operationName, resolvedRole, stagingPlugin, pluginBuildOptions);
            return new PluginPackageWorkspace(cachePath, engine.Version, stagingPlugin.PluginPath, destinationPluginPath, stagingPlugin.Name);
        }

        /// <summary>
        /// Represents one concrete cached project workspace and the materialization rules needed around a direct UBT build.
        /// </summary>
        internal sealed class ProjectBuildWorkspace
        {
            /// <summary>
            /// Captures the stable cache path plus the source and output materialization specs for one direct build cache.
            /// </summary>
            internal ProjectBuildWorkspace(string cachePath, string sourceProjectPath, FileMaterializationSpec projectInputs, FileMaterializationSpec buildOutputs, IReadOnlySet<string> projectPluginRelativePaths, string role)
            {
                CachePath = RequireText(cachePath, nameof(cachePath), "Cached project workspace path is required.");
                SourceProjectPath = RequireText(sourceProjectPath, nameof(sourceProjectPath), "Cached project source path is required.");
                ProjectInputs = projectInputs ?? throw new ArgumentNullException(nameof(projectInputs));
                BuildOutputs = buildOutputs ?? throw new ArgumentNullException(nameof(buildOutputs));
                ProjectPluginRelativePaths = projectPluginRelativePaths ?? throw new ArgumentNullException(nameof(projectPluginRelativePaths));
                Role = RequireText(role, nameof(role), "Cached project build role is required.");
            }

            /// <summary>
            /// Gets the stable cached project root used by the wrapped build operation.
            /// </summary>
            internal string CachePath { get; }

            /// <summary>
            /// Gets the session project path that supplies cache inputs and receives generated build outputs.
            /// </summary>
            private string SourceProjectPath { get; }

            /// <summary>
            /// Gets the materialization rules used to refresh source inputs while preserving reusable Intermediate state.
            /// </summary>
            private FileMaterializationSpec ProjectInputs { get; }

            /// <summary>
            /// Gets the materialization rules used to copy generated build outputs back into the session project.
            /// </summary>
            private FileMaterializationSpec BuildOutputs { get; }

            /// <summary>
            /// Gets the current project plugin directory set relative to the Plugins root, used to remove stale plugin trees.
            /// </summary>
            private IReadOnlySet<string> ProjectPluginRelativePaths { get; }

            /// <summary>
            /// Gets the role text used in failure messages for this cached build workspace.
            /// </summary>
            private string Role { get; }

            /// <summary>
            /// Refreshes the source project into the cache while preserving Unreal's reusable Intermediate tree.
            /// </summary>
            internal async Task PrepareAsync(ExecutionTaskContext context)
            {
                Directory.CreateDirectory(CachePath);
                DeleteStaleCachedPluginDirectories(context);
                context.Logger.LogInformation("Refreshing Unreal build cache workspace from '{SourceProjectPath}' to '{CachedProjectPath}'.", SourceProjectPath, CachePath);
                FileUtils.MaterializeDirectory(SourceProjectPath, CachePath, ProjectInputs, context.Logger, context.CancellationToken, mirrorDirectories: true);

                if (ProjectPaths.Instance.IsTargetDirectory(CachePath))
                {
                    await Task.CompletedTask;
                    return;
                }

                context.Logger.LogWarning("Cached Unreal build workspace '{CachedProjectPath}' was invalid after refresh; recreating it without preserved intermediates.", CachePath);
                FileUtils.DeleteDirectoryIfExists(CachePath);
                Directory.CreateDirectory(CachePath);
                FileUtils.MaterializeDirectory(SourceProjectPath, CachePath, ProjectInputs, context.Logger, context.CancellationToken, mirrorDirectories: true);
                if (!ProjectPaths.Instance.IsTargetDirectory(CachePath))
                {
                    throw new InvalidOperationException($"Cached Unreal build workspace is not a valid project after refresh: {CachePath}");
                }

                await Task.CompletedTask;
            }

            /// <summary>
            /// Removes cached plugin directories that are no longer part of this project shape while preserving output
            /// folders for plugins that are still present at the same Unreal-scanned relative path.
            /// </summary>
            private void DeleteStaleCachedPluginDirectories(ExecutionTaskContext context)
            {
                string cachedPluginsPath = Path.Combine(CachePath, "Plugins");
                if (!Directory.Exists(cachedPluginsPath))
                {
                    return;
                }

                // Delete deeper plugin directories first so grouped plugin trees can be removed without parent conflicts.
                foreach (string pluginDirectoryPath in Directory.GetDirectories(cachedPluginsPath, "*", SearchOption.AllDirectories)
                             .Where(PluginPaths.Instance.IsTargetDirectory)
                             .OrderByDescending(path => path.Length))
                {
                    string relativePluginPath = Path.GetRelativePath(cachedPluginsPath, pluginDirectoryPath);
                    if (!ProjectPluginRelativePaths.Contains(relativePluginPath))
                    {
                        FileUtils.DeleteDirectoryIfExists(pluginDirectoryPath, context.Logger);
                    }
                }
            }

            /// <summary>
            /// Opens the cached project for a wrapped build operation parameter bag.
            /// </summary>
            internal Project OpenCachedProject()
            {
                return CreateRequiredProject(CachePath, $"Cached build workspace is not available for role '{Role}'");
            }

            /// <summary>
            /// Copies generated build outputs from the cache project back to the session project.
            /// </summary>
            internal async Task CopyOutputsAsync(ExecutionTaskContext context)
            {
                using Project cachedProject = CreateRequiredProject(CachePath, "Cached build workspace is not available for output copy");
                context.Logger.LogInformation("Copying cached Unreal build outputs from '{CachedProjectPath}' to session project '{SessionProjectPath}'.", cachedProject.ProjectPath, SourceProjectPath);
                FileUtils.MaterializeDirectory(cachedProject.ProjectPath, SourceProjectPath, BuildOutputs, context.Logger, context.CancellationToken);
                await Task.CompletedTask;
            }
        }

        /// <summary>
        /// Represents one concrete cached BuildPlugin package workspace and its payload copy-back rules.
        /// </summary>
        internal sealed class PluginPackageWorkspace
        {
            /// <summary>
            /// Captures the stable package cache, staging plugin input, and deployment output path for one package run.
            /// </summary>
            internal PluginPackageWorkspace(string cachePath, EngineVersion engineVersion, string stagingPluginPath, string destinationPluginPath, string pluginName)
            {
                CachePath = RequireText(cachePath, nameof(cachePath), "Cached plugin package path is required.");
                EngineVersion = engineVersion ?? throw new ArgumentNullException(nameof(engineVersion));
                StagingPluginPath = RequireText(stagingPluginPath, nameof(stagingPluginPath), "Staged plugin path is required for cached packaging.");
                DestinationPluginPath = RequireText(destinationPluginPath, nameof(destinationPluginPath), "Destination plugin path is required for cached packaging.");
                PluginName = RequireText(pluginName, nameof(pluginName), "Plugin name is required for cached packaging.");
            }

            /// <summary>
            /// Gets the stable cache root used as PackagePlugin's output path.
            /// </summary>
            internal string CachePath { get; }

            /// <summary>
            /// Gets the engine version that package parameters must target.
            /// </summary>
            internal EngineVersion EngineVersion { get; }

            /// <summary>
            /// Gets the staged source-style plugin path consumed by PackagePlugin.
            /// </summary>
            private string StagingPluginPath { get; }

            /// <summary>
            /// Gets the session distributable plugin output path recreated from the package cache payload.
            /// </summary>
            private string DestinationPluginPath { get; }

            /// <summary>
            /// Gets the plugin folder name used to clear stale generated host-project plugin copies.
            /// </summary>
            private string PluginName { get; }

            /// <summary>
            /// Clears stale package payload files while preserving UAT's generated HostProject intermediate tree.
            /// </summary>
            internal async Task PrepareAsync(ExecutionTaskContext context)
            {
                using IDisposable nodeScope = context.Logger.BeginSection("Preparing cached plugin package workspace");
                Directory.CreateDirectory(CachePath);
                FileUtils.DeleteTopLevelFiles(CachePath, new[] { "*" }, context.Logger);
                FileUtils.DeleteTopLevelDirectoriesExcept(CachePath, new[] { BuildPluginHostProjectDirectoryName }, context.Logger);
                FileUtils.DeleteDirectoryIfExists(Path.Combine(CachePath, BuildPluginHostProjectDirectoryName, "Plugins", PluginName), context.Logger);
                await Task.CompletedTask;
            }

            /// <summary>
            /// Opens the staged plugin for the PackagePlugin runtime parameter bag.
            /// </summary>
            internal Plugin OpenStagingPlugin()
            {
                return CreateRequiredPlugin(StagingPluginPath, "Staged plugin is not available for cached packaging");
            }

            /// <summary>
            /// Recreates the session distributable plugin output from the fresh cached package payload.
            /// </summary>
            internal async Task CopyOutputsAsync(ExecutionTaskContext context)
            {
                using IDisposable nodeScope = context.Logger.BeginSection("Copying cached plugin package output");
                if (!Directory.Exists(CachePath))
                {
                    throw new DirectoryNotFoundException($"Cached plugin package output does not exist: {CachePath}");
                }

                FileMaterializationSpec payloadSpec = new();
                payloadSpec.AddRootDirectory(required: true, excludedRelativePaths: new[] { BuildPluginHostProjectDirectoryName });
                FileUtils.DeleteDirectoryIfExists(DestinationPluginPath);
                context.Logger.LogInformation("Copying cached Unreal plugin package output from '{CachedPackagePath}' to '{DestinationPluginPath}'.", CachePath, DestinationPluginPath);
                FileUtils.MaterializeDirectory(CachePath, DestinationPluginPath, payloadSpec, context.Logger, context.CancellationToken);
                using Plugin builtPlugin = CreateRequiredPlugin(DestinationPluginPath, "Built plugin output is missing after cached package copy-back");
                context.Logger.LogInformation($"Validated built plugin output: {builtPlugin.PluginPath}");
                await Task.CompletedTask;
            }
        }

        /// <summary>
        /// Builds a stable opaque identity for one cached project workspace from compile-environment inputs rather than
        /// source content, allowing normal source edits to reuse the same warm Intermediate tree.
        /// </summary>
        internal static string CreateProjectCacheKey(
            Engine engine,
            string operationName,
            string role,
            string subjectName,
            string projectName,
            BuildConfiguration configuration,
            UbtCompiler compiler,
            UbtCppStandard cppStandard,
            IEnumerable<string> shapeParts)
        {
            return CreateCacheKey(
                engine,
                new[]
                {
                    operationName,
                    role,
                    subjectName,
                    projectName,
                    configuration.ToString(),
                    compiler.ToString(),
                    cppStandard.ToString()
                },
                shapeParts);
        }

        /// <summary>
        /// Builds a stable opaque identity for one cached plugin-packaging workspace from packaging-environment inputs
        /// rather than source content, allowing Unreal's generated host-project intermediates to stay warm across runs.
        /// </summary>
        internal static string CreatePluginPackageCacheKey(
            Engine engine,
            string operationName,
            string role,
            string pluginName,
            IEnumerable<string> shapeParts)
        {
            return CreateCacheKey(
                engine,
                new[]
                {
                    operationName,
                    role,
                    pluginName
                },
                shapeParts);
        }

        /// <summary>
        /// Builds the final hash source from engine identity, primary operation identity, and ordered shape parts so cache
        /// keys remain compact while still separating incompatible build environments.
        /// </summary>
        private static string CreateCacheKey(Engine engine, IEnumerable<string> identityParts, IEnumerable<string> shapeParts)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            IEnumerable<string> orderedShapeParts = (shapeParts ?? Array.Empty<string>())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .OrderBy(part => part, StringComparer.OrdinalIgnoreCase);
            string normalizedEnginePath = Path.GetFullPath(engine.TargetPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            string keySource = string.Join("|", new[]
                {
                    normalizedEnginePath,
                    engine.Version.ToString()
                }
                .Concat(identityParts)
                .Concat(orderedShapeParts));

            return ComputeHash(keySource, CacheHashLength);
        }

        /// <summary>
        /// Returns the stable cached project directory for one build identity. The cache key already encodes the readable
        /// build identity, so the on-disk path stays deliberately short for Unreal's deep Intermediate output tree.
        /// </summary>
        internal static string GetProjectWorkspacePath(string cacheKey)
        {
            return Path.Combine(
                global::LocalAutomation.Runtime.OutputPaths.TempRoot(),
                "UnrealCache",
                cacheKey,
                "Project");
        }

        /// <summary>
        /// Returns the stable package directory that UAT BuildPlugin can use as both its output root and generated
        /// host-project cache for one plugin packaging identity.
        /// </summary>
        internal static string GetPluginPackageWorkspacePath(string cacheKey)
        {
            return Path.Combine(
                global::LocalAutomation.Runtime.OutputPaths.TempRoot(),
                "UnrealCache",
                cacheKey,
                "PluginPackage");
        }

        /// <summary>
        /// Builds the stable cached project path from engine identity, build role, source-project shape, and compiler shape.
        /// </summary>
        private static string GetProjectBuildCachePath(
            Engine engine,
            string operationName,
            string role,
            string subjectName,
            Project sourceProject,
            BuildConfiguration configuration,
            UbtCompiler compiler,
            UbtCppStandard cppStandard,
            IEnumerable<string> projectPluginShapeParts)
        {
            string resolvedOperationName = RequireText(operationName, nameof(operationName), "Operation name is required for a cached project build.");
            string resolvedRole = RequireText(role, nameof(role), "Role is required for a cached project build.");
            string resolvedSubjectName = RequireText(subjectName, nameof(subjectName), "Subject name is required for a cached project build.");
            string cacheKey = CreateProjectCacheKey(
                engine,
                resolvedOperationName,
                resolvedRole,
                resolvedSubjectName,
                sourceProject.Name,
                configuration,
                compiler,
                cppStandard,
                projectPluginShapeParts.Select(shapePart => $"ProjectPlugin:{shapePart}"));

            return GetProjectWorkspacePath(cacheKey);
        }

        /// <summary>
        /// Builds a stable BuildPlugin cache path from engine identity, selected platforms, strict mode, and module shape.
        /// </summary>
        private static string GetPluginPackageCachePath(Engine engine, string operationName, string role, Plugin stagingPlugin, PluginBuildOptions pluginBuildOptions)
        {
            string resolvedOperationName = RequireText(operationName, nameof(operationName), "Operation name is required for cached plugin packaging.");
            string resolvedRole = RequireText(role, nameof(role), "Role is required for cached plugin packaging.");
            IEnumerable<string> targetPlatformShape = PluginBuildPlatformValidation.GetSelectedTargetPlatforms(pluginBuildOptions)
                .Select(platform => $"TargetPlatform:{platform}");
            IEnumerable<string> moduleShape = stagingPlugin.PluginDescriptor.Modules
                .Select(module => $"Module:{module.Name}:{module.Type}");
            string cacheKey = CreatePluginPackageCacheKey(
                engine,
                resolvedOperationName,
                resolvedRole,
                stagingPlugin.Name,
                targetPlatformShape
                    .Concat(moduleShape)
                    .Concat(new[] { $"StrictIncludes:{pluginBuildOptions.StrictIncludes}" }));

            return GetPluginPackageWorkspacePath(cacheKey);
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
        /// Computes a stable uppercase hexadecimal hash for compact cache path identity.
        /// </summary>
        private static string ComputeHash(string source, int length)
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(source));
            string hex = BitConverter.ToString(hash).Replace("-", string.Empty, StringComparison.Ordinal);
            return hex.Substring(0, length);
        }
    }
}
