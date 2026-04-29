using System;
using System.IO;
using System.Threading.Tasks;
using LocalAutomation.Core.IO;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon.Unreal
{
    internal static partial class UnrealBuildWorkspaceCache
    {
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
    }
}
