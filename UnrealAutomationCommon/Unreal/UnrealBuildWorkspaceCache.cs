using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
