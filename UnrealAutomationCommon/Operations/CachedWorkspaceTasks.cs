using System;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Operations
{
    /// <summary>
    /// Authors repeated cached-workspace task shape from a data spec plus the wrapped operation's runtime parameter factory.
    /// </summary>
    internal static class CachedWorkspaceTasks
    {
        /// <summary>
        /// Creates a visible cached-workspace parent task with explicit prepare, wrapped operation, and copy-back children.
        /// </summary>
        public static ExecutionTaskBuilder Add(
            ExecutionTaskScopeBuilder scope,
            string title,
            ExecutionLock cacheLock,
            Operation cachedOperation,
            ValidatedOperationParameters operationParameters,
            Func<IOperationParameterContext, CachedWorkspaceSpec> createWorkspaceSpec,
            Func<IOperationParameterContext, CachedWorkspaceSpec, OperationParameters> createRuntimeParameters)
        {
            _ = scope ?? throw new ArgumentNullException(nameof(scope));
            string resolvedTitle = string.IsNullOrWhiteSpace(title)
                ? throw new ArgumentException("A cached workspace task title is required.", nameof(title))
                : title;
            _ = cacheLock ?? throw new ArgumentNullException(nameof(cacheLock));
            Operation resolvedOperation = cachedOperation ?? throw new ArgumentNullException(nameof(cachedOperation));
            _ = operationParameters ?? throw new ArgumentNullException(nameof(operationParameters));
            _ = createWorkspaceSpec ?? throw new ArgumentNullException(nameof(createWorkspaceSpec));
            _ = createRuntimeParameters ?? throw new ArgumentNullException(nameof(createRuntimeParameters));

            ExecutionTaskBuilder parent = scope.Task(resolvedTitle)
                .WithExecutionLocks(cacheLock);
            parent.Children(steps =>
            {
                steps.Task("Prepare Cached Workspace")
                    .Describe("Prepare cache inputs before the cached operation runs")
                    .Run(context => createWorkspaceSpec(context).PrepareAsync(context));

                steps.Task("Run Cached Operation")
                    .Describe($"Run {resolvedOperation.OperationName} against the prepared cache workspace")
                    .AddChildOperation(
                        resolvedOperation,
                        () => CreateAuthoringParameters(operationParameters, resolvedOperation),
                        context => CreateRuntimeParameters(context, resolvedOperation, createWorkspaceSpec, createRuntimeParameters))
                    .HideInGraph();

                steps.Task("Copy Cached Outputs")
                    .Describe("Copy generated cache outputs back into the session workspace")
                    .Run(context => createWorkspaceSpec(context).CopyOutputsAsync(context));
            });

            return parent;
        }

        /// <summary>
        /// Creates authoring-time parameters by selecting the nearest plan target supported by the wrapped operation.
        /// </summary>
        private static OperationParameters CreateAuthoringParameters(ValidatedOperationParameters operationParameters, Operation cachedOperation)
        {
            IOperationTarget planTarget = GetPlanTarget(operationParameters.Target, cachedOperation);
            OperationParameters parameters = operationParameters.CreateChild();
            parameters.Target = planTarget;
            return parameters;
        }

        /// <summary>
        /// Walks the plan target hierarchy until it finds a target that the wrapped operation can declare against.
        /// </summary>
        private static IOperationTarget GetPlanTarget(IOperationTarget sourceTarget, Operation cachedOperation)
        {
            for (IOperationTarget? currentTarget = sourceTarget; currentTarget != null; currentTarget = currentTarget.ParentTarget)
            {
                if (cachedOperation.SupportsTarget(currentTarget))
                {
                    return currentTarget;
                }
            }

            throw new InvalidOperationException($"Cached operation '{cachedOperation.OperationName}' does not support target '{sourceTarget.TypeName}' or any parent target.");
        }

        /// <summary>
        /// Creates and validates runtime parameters for the supplied child operation before the scheduler starts it.
        /// </summary>
        private static OperationParameters CreateRuntimeParameters(
            IOperationParameterContext context,
            Operation cachedOperation,
            Func<IOperationParameterContext, CachedWorkspaceSpec> createWorkspaceSpec,
            Func<IOperationParameterContext, CachedWorkspaceSpec, OperationParameters> createRuntimeParameters)
        {
            CachedWorkspaceSpec workspaceSpec = createWorkspaceSpec(context)
                ?? throw new InvalidOperationException($"Cached operation '{cachedOperation.OperationName}' did not create a workspace spec.");
            OperationParameters parameters = createRuntimeParameters(context, workspaceSpec)
                ?? throw new InvalidOperationException($"Cached operation '{cachedOperation.OperationName}' did not create operation parameters.");

            if (parameters.Target == null)
            {
                throw new InvalidOperationException($"Cached operation '{cachedOperation.OperationName}' did not select a target.");
            }

            if (!cachedOperation.SupportsTarget(parameters.Target))
            {
                throw new InvalidOperationException($"Cached operation '{cachedOperation.OperationName}' does not support runtime target '{parameters.Target.TypeName}'.");
            }

            return parameters;
        }
    }
}
