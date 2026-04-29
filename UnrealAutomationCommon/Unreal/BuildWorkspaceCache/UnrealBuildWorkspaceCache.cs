using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LocalAutomation.Core.IO;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Provides stable project build workspaces for temporary Unreal projects so UBT can reuse its Intermediate tree and
    /// shared PCH outputs across repeated automation runs.
    /// </summary>
    internal static partial class UnrealBuildWorkspaceCache
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
            string sourceProjectPath,
            BuildConfiguration configuration,
            UbtCompiler compiler,
            UbtCppStandard cppStandard)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            using Project sourceProject = CreateRequiredProject(sourceProjectPath, "Cached build source project is not available");
            IReadOnlySet<string> projectPluginRelativePaths = MaterializationSpecs.GetProjectPluginRelativePaths(sourceProject);
            IReadOnlySet<string> projectPluginModuleShape = MaterializationSpecs.GetProjectPluginModuleShape(sourceProject);
            IEnumerable<string> projectShapeParts = GetProjectBuildShapeParts(sourceProject, projectPluginRelativePaths, projectPluginModuleShape);
            IReadOnlySet<string> projectPluginNames = projectPluginRelativePaths
                .Select(Path.GetFileName)
                .Where(pluginName => !string.IsNullOrWhiteSpace(pluginName))
                .Select(pluginName => pluginName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            FileMaterializationSpec projectInputs = MaterializationSpecs.CreateProject(sourceProject, projectPluginNames);
            FileMaterializationSpec buildOutputs = MaterializationSpecs.CreateProjectBuildOutputs(sourceProject);
            string cachePath = GetProjectBuildCachePath(engine, sourceProject, configuration, compiler, cppStandard, projectShapeParts);
            return new ProjectBuildWorkspace(cachePath, sourceProject.ProjectPath, projectInputs, buildOutputs, projectPluginRelativePaths);
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
        /// Builds a stable opaque identity for one cached project workspace from compile-environment inputs rather than
        /// source content, allowing normal source edits to reuse the same warm Intermediate tree.
        /// </summary>
        internal static string CreateProjectCacheKey(
            Engine engine,
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
                    "ProjectBuild",
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
        /// Builds project-shape entries that separate incompatible workspaces without keying on ordinary source file edits.
        /// </summary>
        private static IEnumerable<string> GetProjectBuildShapeParts(Project sourceProject, IEnumerable<string> projectPluginRelativePaths, IEnumerable<string> projectPluginModuleShape)
        {
            // Project module and plugin declarations change Unreal's generated build rules, so they define cache shape.
            IEnumerable<string> projectModules = sourceProject.ProjectDescriptor.Modules
                .Select(module => $"ProjectModule:{module.Name}:{module.Type}");
            IEnumerable<string> projectPluginDependencies = sourceProject.ProjectDescriptor.Plugins
                .Select(plugin => $"ProjectPluginDependency:{plugin.Name}:{plugin.Enabled}");
            IEnumerable<string> projectPlugins = projectPluginRelativePaths
                .Select(relativePath => $"ProjectPlugin:{relativePath}");
            IEnumerable<string> projectPluginModules = projectPluginModuleShape
                .Select(shapePart => $"ProjectPlugin:{shapePart}");
            return projectModules
                .Concat(projectPluginDependencies)
                .Concat(projectPlugins)
                .Concat(projectPluginModules);
        }

        /// <summary>
        /// Builds the stable cached project path from engine identity, project descriptor shape, and compiler shape.
        /// </summary>
        private static string GetProjectBuildCachePath(
            Engine engine,
            Project sourceProject,
            BuildConfiguration configuration,
            UbtCompiler compiler,
            UbtCppStandard cppStandard,
            IEnumerable<string> projectShapeParts)
        {
            string cacheKey = CreateProjectCacheKey(
                engine,
                sourceProject.Name,
                configuration,
                compiler,
                cppStandard,
                projectShapeParts);

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
