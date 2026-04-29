using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Builds stable persistent-workspace key parts for Unreal workflows that preserve reusable generated artifacts.
    /// </summary>
    internal static class UnrealWorkspaceKeys
    {
        /// <summary>
        /// Builds identity parts for a project-root workspace that runs direct UBT project or plugin builds.
        /// </summary>
        internal static IEnumerable<string> ProjectBuild(
            Engine engine,
            Project sourceProject,
            BuildConfiguration configuration,
            UbtCompiler compiler,
            UbtCppStandard cppStandard)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            _ = sourceProject ?? throw new ArgumentNullException(nameof(sourceProject));
            IReadOnlySet<string> projectPluginRelativePaths = MaterializationSpecs.GetProjectPluginRelativePaths(sourceProject);
            IReadOnlySet<string> projectPluginModuleShape = MaterializationSpecs.GetProjectPluginModuleShape(sourceProject);
            IEnumerable<string> projectShapeParts = GetProjectBuildShapeParts(sourceProject, projectPluginRelativePaths, projectPluginModuleShape);
            return new[]
                {
                    "Unreal",
                    "ProjectBuild",
                    NormalizeEnginePath(engine),
                    engine.Version.ToString(),
                    sourceProject.Name,
                    configuration.ToString(),
                    compiler.ToString(),
                    cppStandard.ToString()
                }
                .Concat(GetOrderedShapeParts(projectShapeParts));
        }

        /// <summary>
        /// Builds identity parts for a role-owned project root whose generated outputs can be reused across builds.
        /// </summary>
        internal static IEnumerable<string> ProjectRole(
            Engine engine,
            string operationName,
            string role,
            Project sourceProject)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            _ = sourceProject ?? throw new ArgumentNullException(nameof(sourceProject));
            string resolvedOperationName = RequireText(operationName, nameof(operationName), "Operation name is required for persistent project roles.");
            string resolvedRole = RequireText(role, nameof(role), "Role is required for persistent project workspaces.");
            IReadOnlySet<string> projectPluginRelativePaths = MaterializationSpecs.GetProjectPluginRelativePaths(sourceProject);
            IReadOnlySet<string> projectPluginModuleShape = MaterializationSpecs.GetProjectPluginModuleShape(sourceProject);
            IEnumerable<string> projectShapeParts = GetProjectBuildShapeParts(sourceProject, projectPluginRelativePaths, projectPluginModuleShape);
            return new[]
                {
                    "Unreal",
                    "ProjectRole",
                    NormalizeEnginePath(engine),
                    engine.Version.ToString(),
                    resolvedOperationName,
                    resolvedRole,
                    sourceProject.Name
                }
                .Concat(GetOrderedShapeParts(projectShapeParts));
        }

        /// <summary>
        /// Builds identity parts for a BuildPlugin-style package workspace with a generated host project.
        /// </summary>
        internal static IEnumerable<string> PluginPackage(
            Engine engine,
            string operationName,
            string role,
            string pluginName,
            PluginBuildOptions pluginBuildOptions)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            string resolvedPluginName = RequireText(pluginName, nameof(pluginName), "Plugin name is required for persistent plugin packaging.");
            string resolvedRole = RequireText(role, nameof(role), "Role is required for persistent plugin packaging.");
            _ = pluginBuildOptions ?? throw new ArgumentNullException(nameof(pluginBuildOptions));
            string resolvedOperationName = RequireText(operationName, nameof(operationName), "Operation name is required for persistent plugin packaging.");
            IEnumerable<string> targetPlatformShape = PluginBuildPlatformValidation.GetSelectedTargetPlatforms(pluginBuildOptions)
                .Select(platform => $"TargetPlatform:{platform}");
            return new[]
                {
                    "Unreal",
                    "PluginPackage",
                    NormalizeEnginePath(engine),
                    engine.Version.ToString(),
                    resolvedOperationName,
                    resolvedRole,
                    resolvedPluginName
                }
                .Concat(GetOrderedShapeParts(targetPlatformShape
                    .Concat(new[] { $"StrictIncludes:{pluginBuildOptions.StrictIncludes}" })));
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
        /// Returns non-empty configuration text or throws with a parameter-specific message.
        /// </summary>
        private static string RequireText(string value, string parameterName, string message)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException(message, parameterName)
                : value;
        }

        /// <summary>
        /// Orders unordered shape entries before they become generic persistent-workspace key parts.
        /// </summary>
        private static IEnumerable<string> GetOrderedShapeParts(IEnumerable<string> shapeParts)
        {
            return (shapeParts ?? Array.Empty<string>())
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .OrderBy(part => part, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes engine install paths so equivalent Windows paths share the same workspace identity.
        /// </summary>
        private static string NormalizeEnginePath(Engine engine)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            return Path.GetFullPath(engine.TargetPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
        }
    }
}
