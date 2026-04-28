using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using LocalAutomation.Core.IO;
using Microsoft.Extensions.Logging;
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
        /// Names the generated UAT BuildPlugin host project that must remain in the cache but never enter payload copies.
        /// </summary>
        private const string BuildPluginHostProjectDirectoryName = "HostProject";

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
        /// Clears one cached plugin package root before UAT writes a fresh distributable payload while preserving the
        /// generated host project's Intermediate directory, which is the reusable part of BuildPlugin's packaging flow.
        /// </summary>
        internal static void PreparePluginPackageWorkspace(string cachedPackagePath, string pluginName, ILogger logger)
        {
            _ = logger ?? throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrWhiteSpace(cachedPackagePath))
            {
                throw new ArgumentException("Cached plugin package path is required.", nameof(cachedPackagePath));
            }

            if (string.IsNullOrWhiteSpace(pluginName))
            {
                throw new ArgumentException("Plugin name is required for cached plugin packaging.", nameof(pluginName));
            }

            Directory.CreateDirectory(cachedPackagePath);
            DeletePluginPackagePayload(cachedPackagePath, logger);
            DeleteCachedHostProjectPluginInputs(cachedPackagePath, pluginName, logger);
        }

        /// <summary>
        /// Copies the freshly produced distributable plugin payload out of a cached package root without copying the
        /// generated host project that UAT keeps beside the payload for future incremental builds.
        /// </summary>
        internal static void CopyPluginPackageOutput(string cachedPackagePath, string destinationPluginPath, ILogger logger, CancellationToken cancellationToken)
        {
            _ = logger ?? throw new ArgumentNullException(nameof(logger));
            if (string.IsNullOrWhiteSpace(cachedPackagePath))
            {
                throw new ArgumentException("Cached plugin package path is required.", nameof(cachedPackagePath));
            }

            if (string.IsNullOrWhiteSpace(destinationPluginPath))
            {
                throw new ArgumentException("Destination plugin path is required.", nameof(destinationPluginPath));
            }

            if (!Directory.Exists(cachedPackagePath))
            {
                throw new DirectoryNotFoundException($"Cached plugin package output does not exist: {cachedPackagePath}");
            }

            logger.LogInformation("Copying cached Unreal plugin package output from '{CachedPackagePath}' to '{DestinationPluginPath}'.", cachedPackagePath, destinationPluginPath);
            FileMaterializationSpec payloadSpec = new();
            payloadSpec.AddRootDirectory(required: true, excludedRelativePaths: new[] { BuildPluginHostProjectDirectoryName });
            FileUtils.MaterializeDirectory(cachedPackagePath, destinationPluginPath, payloadSpec, logger, cancellationToken);
        }

        /// <summary>
        /// Refreshes authored project inputs into a stable cache path without deleting Intermediate, then returns a project
        /// target rooted at that cached path for direct UBT/UAT build operations.
        /// </summary>
        internal static Project PrepareProjectWorkspace(Project sourceProject, string cachedProjectPath, FileMaterializationSpec materializationSpec, ILogger logger, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(cachedProjectPath);
            DeleteVolatileProjectState(cachedProjectPath, logger);
            DeleteRefreshableProjectInputs(cachedProjectPath, logger);
            logger.LogInformation("Refreshing Unreal build cache workspace from '{SourceProjectPath}' to '{CachedProjectPath}'.", sourceProject.ProjectPath, cachedProjectPath);
            FileUtils.MaterializeDirectory(sourceProject.ProjectPath, cachedProjectPath, materializationSpec, logger, cancellationToken);

            Project cachedProject = new(cachedProjectPath);
            if (cachedProject.IsValid)
            {
                return cachedProject;
            }

            cachedProject.Dispose();
            logger.LogWarning("Cached Unreal build workspace '{CachedProjectPath}' was invalid after refresh; recreating it without preserved intermediates.", cachedProjectPath);
            FileUtils.DeleteDirectoryIfExists(cachedProjectPath);
            Directory.CreateDirectory(cachedProjectPath);
            FileUtils.MaterializeDirectory(sourceProject.ProjectPath, cachedProjectPath, materializationSpec, logger, cancellationToken);

            cachedProject = new Project(cachedProjectPath);
            if (!cachedProject.IsValid)
            {
                cachedProject.Dispose();
                throw new InvalidOperationException($"Cached Unreal build workspace is not a valid project after refresh: {cachedProjectPath}");
            }

            return cachedProject;
        }

        /// <summary>
        /// Removes project state that should never be reused from the build cache because it belongs to one editor, launch,
        /// cook, or package attempt rather than to UBT's reusable compile intermediates.
        /// </summary>
        private static void DeleteVolatileProjectState(string cachedProjectPath, ILogger logger)
        {
            /* Compile acceleration comes from preserving Intermediate. Binaries and Build outputs are recreated or
               refreshed by each direct build so stale receipts and DLLs never leak out of an old cache run. */
            DeleteCachedDirectory(cachedProjectPath, "Binaries", logger);
            DeleteCachedDirectory(cachedProjectPath, "Build", logger);
            DeleteCachedDirectory(cachedProjectPath, "Saved", logger);
            DeleteCachedDirectory(cachedProjectPath, "DerivedDataCache", logger);
        }

        /// <summary>
        /// Removes authored inputs before materialization so deleted source files and removed plugins do not linger in the
        /// cached workspace while UBT's reusable Intermediate folder remains intact.
        /// </summary>
        private static void DeleteRefreshableProjectInputs(string cachedProjectPath, ILogger logger)
        {
            DeleteCachedDirectory(cachedProjectPath, "Config", logger);
            DeleteCachedDirectory(cachedProjectPath, "Source", logger);
            DeleteCachedDirectory(cachedProjectPath, "Content", logger);
            DeleteCachedDirectory(cachedProjectPath, "Plugins", logger);

            foreach (string projectDescriptorPath in Directory.GetFiles(cachedProjectPath, "*.uproject", SearchOption.TopDirectoryOnly))
            {
                logger.LogInformation("Deleting cached project descriptor before refresh: {ProjectDescriptorPath}", projectDescriptorPath);
                FileUtils.DeleteFileIfExists(projectDescriptorPath);
            }

            foreach (string projectIconPath in Directory.GetFiles(cachedProjectPath, "*.png", SearchOption.TopDirectoryOnly))
            {
                logger.LogInformation("Deleting cached project icon before refresh: {ProjectIconPath}", projectIconPath);
                FileUtils.DeleteFileIfExists(projectIconPath);
            }
        }

        /// <summary>
        /// Deletes one known cache subdirectory when present and logs the action so cache refreshes stay diagnosable.
        /// </summary>
        private static void DeleteCachedDirectory(string cachedProjectPath, string relativePath, ILogger logger)
        {
            string absolutePath = Path.Combine(cachedProjectPath, relativePath);
            if (!Directory.Exists(absolutePath))
            {
                return;
            }

            logger.LogInformation("Deleting cached Unreal project directory before refresh: {CachedDirectoryPath}", absolutePath);
            FileUtils.DeleteDirectoryIfExists(absolutePath);
        }

        /// <summary>
        /// Deletes the distributable payload files from a cached plugin package root while leaving UAT's generated
        /// HostProject directory available for incremental compilation state.
        /// </summary>
        private static void DeletePluginPackagePayload(string cachedPackagePath, ILogger logger)
        {
            foreach (string filePath in Directory.GetFiles(cachedPackagePath, "*", SearchOption.TopDirectoryOnly))
            {
                logger.LogInformation("Deleting cached plugin package file before refresh: {CachedPackageFilePath}", filePath);
                FileUtils.DeleteFileIfExists(filePath);
            }

            foreach (string directoryPath in Directory.GetDirectories(cachedPackagePath, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsBuildPluginHostProjectDirectory(Path.GetFileName(directoryPath)))
                {
                    continue;
                }

                logger.LogInformation("Deleting cached plugin package directory before refresh: {CachedPackageDirectoryPath}", directoryPath);
                FileUtils.DeleteDirectoryIfExists(directoryPath);
            }
        }

        /// <summary>
        /// Removes copied plugin inputs from the cached UAT host project so deleted source files cannot linger while the
        /// host project's own Intermediate tree remains available for UnrealBuildTool reuse.
        /// </summary>
        private static void DeleteCachedHostProjectPluginInputs(string cachedPackagePath, string pluginName, ILogger logger)
        {
            string cachedHostProjectPluginPath = Path.Combine(cachedPackagePath, BuildPluginHostProjectDirectoryName, "Plugins", pluginName);
            if (!Directory.Exists(cachedHostProjectPluginPath))
            {
                return;
            }

            logger.LogInformation("Deleting cached BuildPlugin host-project plugin inputs before refresh: {CachedHostProjectPluginPath}", cachedHostProjectPluginPath);
            FileUtils.DeleteDirectoryIfExists(cachedHostProjectPluginPath);
        }

        /// <summary>
        /// Identifies the generated host project UAT BuildPlugin creates beside the distributable payload.
        /// </summary>
        private static bool IsBuildPluginHostProjectDirectory(string directoryName)
        {
            return string.Equals(directoryName, BuildPluginHostProjectDirectoryName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Copies generated project and plugin build outputs from the cached build project back into the session project
        /// that package, launch, and archive tasks read from.
        /// </summary>
        internal static void CopyProjectBuildOutputs(Project cachedProject, Project sessionProject, ILogger logger, CancellationToken cancellationToken)
        {
            FileMaterializationSpec buildOutputs = MaterializationSpecs.CreateProjectBuildOutputs(cachedProject);
            /* Session projects accumulate outputs from several independent build steps: editor receipts, game receipts,
               project plugin binaries, and package-specific binaries can all be required by later AutomationTool phases.
               Copy-back therefore merges cached build outputs into the session tree instead of replacing whole output
               folders, while the cached workspace itself is still cleaned before each direct build. */
            logger.LogInformation("Copying cached Unreal build outputs from '{CachedProjectPath}' to session project '{SessionProjectPath}'.", cachedProject.ProjectPath, sessionProject.ProjectPath);
            FileUtils.MaterializeDirectory(cachedProject.ProjectPath, sessionProject.ProjectPath, buildOutputs, logger, cancellationToken);
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
