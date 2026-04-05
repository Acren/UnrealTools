using System;
using LocalAutomation.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Builds authored execution plans through the shared framework-owned authoring pipeline used by both preview and
/// runtime execution.
/// </summary>
public static class ExecutionPlanFactory
{
    /// <summary>
    /// Builds the preview/runtime plan for any operation using the shared framework-owned authoring pipeline.
    /// </summary>
    public static ExecutionPlan? BuildPlan(Operation operation, OperationParameters operationParameters)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (operationParameters == null)
        {
            throw new ArgumentNullException(nameof(operationParameters));
        }

        if (operationParameters.Target == null)
        {
            return null;
        }

        return BuildWrappedPlan(operation, operationParameters, NullLogger.Instance);
    }

    /// <summary>
    /// Builds one operation plan and wraps every executable task callback so task execution happens inside a
    /// framework-owned operation execution context.
    /// </summary>
    internal static ExecutionPlan? BuildWrappedPlan(Operation operation, OperationParameters operationParameters, ILogger logger)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        if (operationParameters == null)
        {
            throw new ArgumentNullException(nameof(operationParameters));
        }

        if (logger == null)
        {
            throw new ArgumentNullException(nameof(logger));
        }

        if (operationParameters.Target == null)
        {
            return null;
        }

        /* Child-operation expansion must recurse back through the same wrapped authoring path so every executable task,
           including dynamically inserted descendants, runs inside the framework-owned runtime context. */
        ExecutionPlanId planId = ExecutionIdentifierFactory.CreatePlanId(operation.GetType().Name);
        ExecutionPlanBuilder builder = new(operation.OperationName, planId, (childOperation, childParameters) => BuildWrappedPlan(childOperation, childParameters, logger));
        builder.SetOperation(operation);
        builder.SetBuilderOperationParameters(operationParameters);
        builder.SetDeclaredOptionTypes(operation.GetRequiredOptionSetTypes(operationParameters.Target));
        ExecutionTaskBuilder root = builder.Task(operation.OperationName, operationParameters.Target.DisplayName, default);
        operation.DescribeExecutionPlan(operation.ValidateParameters(operationParameters), root);
        return builder.BuildPlan();
    }
}
