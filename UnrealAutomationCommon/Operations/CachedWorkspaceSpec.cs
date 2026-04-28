using System;
using System.Threading.Tasks;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Describes one cache workspace and the two lifecycle steps that surround the wrapped operation.
    /// </summary>
    internal sealed class CachedWorkspaceSpec
    {
        /// <summary>
        /// Refreshes the cache workspace before the wrapped operation consumes it.
        /// </summary>
        private readonly Func<ExecutionTaskContext, Task> _prepareAsync;

        /// <summary>
        /// Copies generated outputs from the cache workspace back into the session workspace.
        /// </summary>
        private readonly Func<ExecutionTaskContext, Task> _copyOutputsAsync;

        /// <summary>
        /// Captures the stable cache path and lifecycle behavior for one cached operation.
        /// </summary>
        public CachedWorkspaceSpec(string cachePath, Func<ExecutionTaskContext, Task> prepareAsync, Func<ExecutionTaskContext, Task> copyOutputsAsync)
        {
            CachePath = string.IsNullOrWhiteSpace(cachePath)
                ? throw new ArgumentException("A cached workspace path is required.", nameof(cachePath))
                : cachePath;
            _prepareAsync = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
            _copyOutputsAsync = copyOutputsAsync ?? throw new ArgumentNullException(nameof(copyOutputsAsync));
        }

        /// <summary>
        /// Gets the root path that the wrapped operation should treat as its cache workspace.
        /// </summary>
        public string CachePath { get; }

        /// <summary>
        /// Refreshes cache inputs before the wrapped operation runs.
        /// </summary>
        public Task PrepareAsync(ExecutionTaskContext context)
        {
            return _prepareAsync(context ?? throw new ArgumentNullException(nameof(context)));
        }

        /// <summary>
        /// Copies generated cache outputs back to the session workspace.
        /// </summary>
        public Task CopyOutputsAsync(ExecutionTaskContext context)
        {
            return _copyOutputsAsync(context ?? throw new ArgumentNullException(nameof(context)));
        }
    }
}
