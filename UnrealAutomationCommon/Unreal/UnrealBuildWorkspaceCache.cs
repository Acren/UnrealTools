using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
        /// Stores one in-process semaphore per stable cache key so refresh, build, and copy-back remain single-writer for
        /// each cached project path while unrelated cache keys can still proceed independently.
        /// </summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProjectWorkspaceLocks = new(StringComparer.Ordinal);

        /// <summary>
        /// Keeps cache hash folders short while leaving enough entropy that unrelated build identities do not collide in
        /// the persistent Unreal build cache root.
        /// </summary>
        private const int CacheHashLength = 16;

        /// <summary>
        /// Refreshes a stable cached project workspace, runs a build against that cached path, and copies build outputs
        /// back to the session project that downstream package and launch steps continue to use.
        /// </summary>
        public static async Task RunProjectBuildAsync(
            Engine engine,
            Project sourceProject,
            string operationName,
            string role,
            string subjectName,
            BuildConfiguration configuration,
            UbtCompiler compiler,
            UbtCppStandard cppStandard,
            FileMaterializationSpec materializationSpec,
            IEnumerable<string> shapeParts,
            Func<Project, Task> buildAsync,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (engine == null)
            {
                throw new ArgumentNullException(nameof(engine));
            }

            if (sourceProject == null)
            {
                throw new ArgumentNullException(nameof(sourceProject));
            }

            if (materializationSpec == null)
            {
                throw new ArgumentNullException(nameof(materializationSpec));
            }
            if (buildAsync == null)
            {
                throw new ArgumentNullException(nameof(buildAsync));
            }
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            string cacheKey = CreateProjectCacheKey(engine, operationName, role, subjectName, sourceProject.Name, configuration, compiler, cppStandard, shapeParts);
            string cachedProjectPath = GetProjectWorkspacePath(cacheKey);
            SemaphoreSlim cacheLock = ProjectWorkspaceLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));

            logger.LogInformation("Waiting for Unreal build workspace cache '{CacheKey}' at '{CachePath}'.", cacheKey, cachedProjectPath);
            await cacheLock.WaitAsync(cancellationToken);
            try
            {
                logger.LogInformation("Using Unreal build workspace cache '{CacheKey}' at '{CachePath}'.", cacheKey, cachedProjectPath);
                using Project cachedProject = PrepareProjectWorkspace(sourceProject, cachedProjectPath, materializationSpec, logger, cancellationToken);
                await buildAsync(cachedProject);
                CopyProjectBuildOutputs(cachedProject, sourceProject, logger, cancellationToken);
            }
            finally
            {
                cacheLock.Release();
            }
        }

        /// <summary>
        /// Builds a stable opaque identity for one cached project workspace from compile-environment inputs rather than
        /// source content, allowing normal source edits to reuse the same warm Intermediate tree.
        /// </summary>
        private static string CreateProjectCacheKey(
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
            IEnumerable<string> orderedShapeParts = (shapeParts ?? Array.Empty<string>())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .OrderBy(part => part, StringComparer.OrdinalIgnoreCase);
            string normalizedEnginePath = Path.GetFullPath(engine.TargetPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            string keySource = string.Join("|", new[]
                {
                    normalizedEnginePath,
                    engine.Version.ToString(),
                    operationName,
                    role,
                    subjectName,
                    projectName,
                    configuration.ToString(),
                    compiler.ToString(),
                    cppStandard.ToString()
                }.Concat(orderedShapeParts));

            return ComputeHash(keySource, CacheHashLength);
        }

        /// <summary>
        /// Returns the stable cached project directory for one build identity. The cache key already encodes the readable
        /// build identity, so the on-disk path stays deliberately short for Unreal's deep Intermediate output tree.
        /// </summary>
        private static string GetProjectWorkspacePath(string cacheKey)
        {
            return Path.Combine(
                global::LocalAutomation.Runtime.OutputPaths.TempRoot(),
                "UnrealCache",
                cacheKey,
                "Project");
        }

        /// <summary>
        /// Refreshes authored project inputs into a stable cache path without deleting Intermediate, then returns a project
        /// target rooted at that cached path for direct UBT/UAT build operations.
        /// </summary>
        private static Project PrepareProjectWorkspace(Project sourceProject, string cachedProjectPath, FileMaterializationSpec materializationSpec, ILogger logger, CancellationToken cancellationToken)
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
        /// Copies generated project and plugin build outputs from the cached build project back into the session project
        /// that package, launch, and archive tasks read from.
        /// </summary>
        private static void CopyProjectBuildOutputs(Project cachedProject, Project sessionProject, ILogger logger, CancellationToken cancellationToken)
        {
            FileMaterializationSpec buildOutputs = MaterializationSpecs.CreateProjectBuildOutputs(cachedProject);
            DeleteDestinationBuildOutputsWithSource(cachedProject.ProjectPath, sessionProject.ProjectPath, buildOutputs, logger);
            logger.LogInformation("Copying cached Unreal build outputs from '{CachedProjectPath}' to session project '{SessionProjectPath}'.", cachedProject.ProjectPath, sessionProject.ProjectPath);
            FileUtils.MaterializeDirectory(cachedProject.ProjectPath, sessionProject.ProjectPath, buildOutputs, logger, cancellationToken);
        }

        /// <summary>
        /// Clears only destination build-output entries that exist in the cached project, avoiding stale generated files
        /// without deleting optional authored folders that the cached build did not produce.
        /// </summary>
        private static void DeleteDestinationBuildOutputsWithSource(string sourceRootPath, string destinationRootPath, FileMaterializationSpec buildOutputs, ILogger logger)
        {
            foreach (FileMaterializationEntry entry in buildOutputs.Entries)
            {
                string sourcePath = Path.Combine(sourceRootPath, entry.RelativePath);
                string destinationPath = Path.Combine(destinationRootPath, entry.RelativePath);
                if (Directory.Exists(sourcePath))
                {
                    logger.LogInformation("Deleting session build-output directory before copy-back: {DestinationPath}", destinationPath);
                    FileUtils.DeleteDirectoryIfExists(destinationPath);
                    continue;
                }

                if (File.Exists(sourcePath))
                {
                    logger.LogInformation("Deleting session build-output file before copy-back: {DestinationPath}", destinationPath);
                    FileUtils.DeleteFileIfExists(destinationPath);
                }
            }
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
