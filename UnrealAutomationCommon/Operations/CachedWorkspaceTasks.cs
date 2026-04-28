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
            ExecutionLock cacheLock,
            Operation cachedOperation,
            ValidatedOperationParameters operationParameters,
            Func<ExecutionTaskContext, TWorkspace> createWorkspace,
            Func<TWorkspace, ExecutionTaskContext, Task> prepareAsync,
            Func<IOperationParameterContext, TWorkspace, OperationParameters> createRuntimeParameters,
            Func<TWorkspace, ExecutionTaskContext, Task> copyOutputsAsync)
            where TWorkspace : class
        {
            _ = scope ?? throw new ArgumentNullException(nameof(scope));
            string resolvedTitle = string.IsNullOrWhiteSpace(title)
                ? throw new ArgumentException("A cached operation title is required.", nameof(title))
                : title;
            _ = cacheLock ?? throw new ArgumentNullException(nameof(cacheLock));
            Operation resolvedOperation = cachedOperation ?? throw new ArgumentNullException(nameof(cachedOperation));
            _ = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
            _ = createWorkspace ?? throw new ArgumentNullException(nameof(createWorkspace));
            _ = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
            _ = createRuntimeParameters ?? throw new ArgumentNullException(nameof(createRuntimeParameters));
            _ = copyOutputsAsync ?? throw new ArgumentNullException(nameof(copyOutputsAsync));
            TWorkspace? workspace = null;

            // Sibling run/copy tasks rely on the prepare task to publish the concrete workspace first.
            TWorkspace PreparedWorkspace()
            {
                return workspace ?? throw new InvalidOperationException($"Cached workspace for '{resolvedTitle}' was requested before the prepare step completed.");
            }

            // The parent lock protects the whole refresh/run/copy sequence for one stable cache identity.
            ExecutionTaskBuilder parent = scope.Task(resolvedTitle)
                .WithExecutionLocks(cacheLock);
            parent.Children(steps =>
            {
                steps.Task("Prepare Cached Workspace")
                    .Describe("Prepare cache inputs before the cached operation runs")
                    .Run(context =>
                    {
                        workspace = createWorkspace(context)
                            ?? throw new InvalidOperationException($"Cached workspace for '{resolvedTitle}' was not created.");
                        return prepareAsync(workspace, context);
                    });

                steps.Task("Run Cached Operation")
                    .Describe($"Run {resolvedOperation.OperationName} against the prepared cache workspace")
                    .AddChildOperation(
                        resolvedOperation,
                        () => CreateAuthoringParameters(operationParameters, resolvedOperation),
                        context =>
                        {
                            OperationParameters parameters = createRuntimeParameters(context, PreparedWorkspace())
                                ?? throw new InvalidOperationException($"Cached operation '{resolvedOperation.OperationName}' did not create operation parameters.");
                            ValidateRuntimeTarget(resolvedOperation, parameters);
                            return parameters;
                        })
                    .HideInGraph();

                steps.Task("Copy Cached Outputs")
                    .Describe("Copy generated cache outputs back into the session workspace")
                    .Run(context => copyOutputsAsync(PreparedWorkspace(), context));
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

        /// <summary>
        /// Creates authoring-time parameters by selecting the nearest plan target supported by the wrapped operation.
        /// </summary>
        private static OperationParameters CreateAuthoringParameters(ValidatedOperationParameters operationParameters, Operation cachedOperation)
        {
            for (IOperationTarget? currentTarget = operationParameters.Target; currentTarget != null; currentTarget = currentTarget.ParentTarget)
            {
                if (cachedOperation.SupportsTarget(currentTarget))
                {
                    OperationParameters parameters = operationParameters.CreateChild();
                    parameters.Target = currentTarget;
                    return parameters;
                }
            }

            throw new InvalidOperationException($"Cached operation '{cachedOperation.OperationName}' does not support target '{operationParameters.Target.TypeName}' or any parent target.");
        }
    }
}
