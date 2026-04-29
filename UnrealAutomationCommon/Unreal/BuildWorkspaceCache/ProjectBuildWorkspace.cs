using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LocalAutomation.Core.IO;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;

namespace UnrealAutomationCommon.Unreal
{
    internal static partial class UnrealBuildWorkspaceCache
    {
        /// <summary>
        /// Represents one concrete cached project workspace and the materialization rules needed around a direct UBT build.
        /// </summary>
        internal sealed class ProjectBuildWorkspace
        {
            /// <summary>
            /// Captures the stable cache path plus the source and output materialization specs for one direct build cache.
            /// </summary>
            internal ProjectBuildWorkspace(string cachePath, string sourceProjectPath, FileMaterializationSpec projectInputs, FileMaterializationSpec buildOutputs, IReadOnlySet<string> projectPluginRelativePaths)
            {
                CachePath = RequireText(cachePath, nameof(cachePath), "Cached project workspace path is required.");
                SourceProjectPath = RequireText(sourceProjectPath, nameof(sourceProjectPath), "Cached project source path is required.");
                ProjectInputs = projectInputs ?? throw new ArgumentNullException(nameof(projectInputs));
                BuildOutputs = buildOutputs ?? throw new ArgumentNullException(nameof(buildOutputs));
                ProjectPluginRelativePaths = projectPluginRelativePaths ?? throw new ArgumentNullException(nameof(projectPluginRelativePaths));
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
                return CreateRequiredProject(CachePath, "Cached build workspace is not available");
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
    }
}
