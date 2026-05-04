using System;
using System.Collections.Generic;
using System.IO;
using LocalAutomation.Runtime;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Builds stable persistent-workspace key parts for Unreal workflows that preserve reusable generated artifacts.
    /// </summary>
    internal static class UnrealWorkspaceKeys
    {
        /// <summary>
        /// Builds identity parts for a persistent project root from explicit purpose dimensions rather than copied source
        /// layout. Source and plugin directory changes are refreshed by materialization before the workspace is used.
        /// </summary>
        internal static PersistentWorkspaceKey ProjectWorkspace(
            Engine engine,
            Project sourceProject,
            string? workspaceNamespace = null,
            BuildConfiguration? configuration = null,
            UbtCompiler? compiler = null,
            UbtCppStandard? cppStandard = null)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            _ = sourceProject ?? throw new ArgumentNullException(nameof(sourceProject));
            string normalizedEnginePath = NormalizeEnginePath(engine);
            string engineVersion = engine.Version.MajorMinorString;

            // All project workspaces are scoped by engine install and source project name; optional dimensions describe why
            // the root exists without turning refreshed source layout into a cache-busting key input.
            List<string> keyParts = new()
            {
                "Unreal",
                "ProjectWorkspace",
                normalizedEnginePath,
                engineVersion,
                sourceProject.Name
            };
            List<KeyValuePair<string, string>> components = new()
            {
                Component("Domain", "Unreal"),
                Component("WorkspaceType", "ProjectWorkspace"),
                Component("EnginePath", normalizedEnginePath),
                Component("EngineVersion", engineVersion),
                Component("ProjectName", sourceProject.Name)
            };

            if (workspaceNamespace != null)
            {
                // Namespace is the caller-owned string partition for persistent roots that share the same engine/project.
                string resolvedWorkspaceNamespace = RequireText(workspaceNamespace, nameof(workspaceNamespace), "Workspace namespace is required when provided for a persistent project workspace.");
                keyParts.Add(resolvedWorkspaceNamespace);
                components.Add(Component("Namespace", resolvedWorkspaceNamespace));
            }

            if (configuration != null || compiler != null || cppStandard != null)
            {
                BuildConfiguration resolvedConfiguration = configuration ?? throw new ArgumentException("Build configuration is required when persistent project workspace compile settings are provided.", nameof(configuration));
                UbtCompiler resolvedCompiler = compiler ?? throw new ArgumentException("Compiler is required when persistent project workspace compile settings are provided.", nameof(compiler));
                UbtCppStandard resolvedCppStandard = cppStandard ?? throw new ArgumentException("C++ standard is required when persistent project workspace compile settings are provided.", nameof(cppStandard));
                keyParts.Add(resolvedConfiguration.ToString());
                keyParts.Add(resolvedCompiler.ToString());
                keyParts.Add(resolvedCppStandard.ToString());
                components.Add(Component("Configuration", resolvedConfiguration.ToString()));
                components.Add(Component("Compiler", resolvedCompiler.ToString()));
                components.Add(Component("CppStandard", resolvedCppStandard.ToString()));
            }

            return new PersistentWorkspaceKey(keyParts, components);
        }

        /// <summary>
        /// Builds identity parts for a BuildPlugin-style package workspace with a generated host project.
        /// </summary>
        internal static PersistentWorkspaceKey PluginPackage(
            Engine engine,
            string workspaceNamespace,
            string pluginName)
        {
            _ = engine ?? throw new ArgumentNullException(nameof(engine));
            string resolvedPluginName = RequireText(pluginName, nameof(pluginName), "Plugin name is required for persistent plugin packaging.");
            string resolvedWorkspaceNamespace = RequireText(workspaceNamespace, nameof(workspaceNamespace), "Workspace namespace is required for persistent plugin packaging.");
            string normalizedEnginePath = NormalizeEnginePath(engine);
            string engineVersion = engine.Version.MajorMinorString;
            string[] keyParts =
                {
                    "Unreal",
                    "PluginPackage",
                    normalizedEnginePath,
                    engineVersion,
                    resolvedWorkspaceNamespace,
                    resolvedPluginName
                };
            List<KeyValuePair<string, string>> components = new()
            {
                Component("Domain", "Unreal"),
                Component("WorkspaceType", "PluginPackage"),
                Component("EnginePath", normalizedEnginePath),
                Component("EngineVersion", engineVersion),
                Component("Namespace", resolvedWorkspaceNamespace),
                Component("PluginName", resolvedPluginName)
            };
            return new PersistentWorkspaceKey(keyParts, components);
        }

        /// <summary>
        /// Creates one labeled key component for persistent-workspace logs and metadata.
        /// </summary>
        private static KeyValuePair<string, string> Component(string name, string value)
        {
            return new KeyValuePair<string, string>(name, value);
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
