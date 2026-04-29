using System;
using System.Threading.Tasks;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Authors the common cached-workspace task shape while domain-specific callers own cache identity and workspace data.
    /// </summary>
    internal static class CachedWorkspaceTasks
    {
        /// <summary>
        /// Creates a visible cached-operation parent with explicit prepare, wrapped operation, and copy-back children.
        /// </summary>
        public static ExecutionTaskBuilder Add<TWorkspace>(
            ExecutionTaskScopeBuilder scope,
            string title,
            Func<TWorkspace, ExecutionLock> createCacheLock,
            Operation cachedOperation,
            Func<IOperationParameterContext, OperationParameters> createSourceParameters,
            Func<OperationParameters, TWorkspace> createWorkspace,
            Func<TWorkspace, ExecutionTaskContext, Task> prepareAsync,
            Func<TWorkspace, OperationParameters, OperationParameters> createRuntimeParameters,
            Func<TWorkspace, ExecutionTaskContext, Task> copyOutputsAsync)
            where TWorkspace : class
        {
            _ = scope ?? throw new ArgumentNullException(nameof(scope));
            string resolvedTitle = string.IsNullOrWhiteSpace(title)
                ? throw new ArgumentException("A cached operation title is required.", nameof(title))
                : title;
            _ = createCacheLock ?? throw new ArgumentNullException(nameof(createCacheLock));
            Operation resolvedOperation = cachedOperation ?? throw new ArgumentNullException(nameof(cachedOperation));
            _ = createSourceParameters ?? throw new ArgumentNullException(nameof(createSourceParameters));
            _ = createWorkspace ?? throw new ArgumentNullException(nameof(createWorkspace));
            _ = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
            _ = createRuntimeParameters ?? throw new ArgumentNullException(nameof(createRuntimeParameters));
            _ = copyOutputsAsync ?? throw new ArgumentNullException(nameof(copyOutputsAsync));
            OperationParameters? sourceParameters = null;
            TWorkspace? workspace = null;

            // The source parameters describe the prepared artifact that will be mirrored into the cache workspace.
            OperationParameters GetOrCreateSourceParameters(IOperationParameterContext context)
            {
                sourceParameters ??= createSourceParameters(context)
                    ?? throw new InvalidOperationException($"Cached operation '{resolvedOperation.OperationName}' did not create operation parameters.");
                ValidateRuntimeTarget(resolvedOperation, sourceParameters);
                return sourceParameters;
            }

            // Lock resolution can happen before the prepare task, so the workspace is created lazily from shared task data.
            TWorkspace GetOrCreateWorkspace(IOperationParameterContext context)
            {
                return workspace ??= createWorkspace(GetOrCreateSourceParameters(context))
                    ?? throw new InvalidOperationException($"Cached workspace for '{resolvedTitle}' was not created.");
            }

            // The parent lock is derived from the concrete workspace so exact cache identities serialize precisely.
            ExecutionLock ResolveCacheLock(IOperationParameterContext context)
            {
                return createCacheLock(GetOrCreateWorkspace(context))
                    ?? throw new InvalidOperationException($"Cached workspace '{resolvedTitle}' did not create an execution lock.");
            }

            // The parent lock protects the whole refresh/run/copy sequence for one stable cache identity.
            ExecutionTaskBuilder parent = scope.Task(resolvedTitle)
                .WithExecutionLocks(context => new[] { ResolveCacheLock(context) });
            parent.Children(steps =>
            {
                steps.Task("Prepare Cached Workspace")
                    .Describe("Prepare cache inputs before the cached operation runs")
                    .Run(context =>
                    {
                        workspace = GetOrCreateWorkspace(context);
                        return prepareAsync(workspace, context);
                    });

                steps.Task("Run Cached Operation")
                    .Describe($"Run {resolvedOperation.OperationName} against the prepared cache workspace")
                    .Run(async context =>
                    {
                        OperationParameters parameters = createRuntimeParameters(GetOrCreateWorkspace(context), GetOrCreateSourceParameters(context))
                            ?? throw new InvalidOperationException($"Cached operation '{resolvedOperation.OperationName}' did not create operation parameters.");
                        ValidateRuntimeTarget(resolvedOperation, parameters);
                        return await context.RunChildOperationAsync(resolvedOperation, parameters, hideChildOperationRootInGraph: true).ConfigureAwait(false);
                    });

                steps.Task("Copy Cached Outputs")
                    .Describe("Copy generated cache outputs back into the session workspace")
                    .Run(context => copyOutputsAsync(GetOrCreateWorkspace(context), context));
            });

            return parent;
        }

        /// <summary>
        /// Validates that runtime parameters selected a target the wrapped cached operation can execute against.
        /// </summary>
        internal static void ValidateRuntimeTarget(Operation operation, OperationParameters parameters)
        {
            if (parameters.Target == null)
            {
                throw new InvalidOperationException($"Cached operation '{operation.OperationName}' did not select a target.");
            }

            if (!operation.SupportsTarget(parameters.Target))
            {
                throw new InvalidOperationException($"Cached operation '{operation.OperationName}' does not support runtime target '{parameters.Target.TypeName}'.");
            }
        }

    }
}
