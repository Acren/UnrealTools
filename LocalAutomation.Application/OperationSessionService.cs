using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Runtime;

namespace LocalAutomation.Application;

/// <summary>
/// Coordinates operation selection and derived UI state so desktop shells can consume a shared application model
/// instead of recomputing the same operation rules inside window code.
/// </summary>
public sealed class OperationSessionService
{
    private readonly OperationCatalogService _catalog;
    private readonly OperationRuntimeService _runtime;

    /// <summary>
    /// Creates an operation session service from the shared catalog and runtime services.
    /// </summary>
    public OperationSessionService(OperationCatalogService catalog, OperationRuntimeService runtime)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    /// <summary>
    /// Ensures the selected operation type remains compatible with the current target selection.
    /// </summary>
    public Type? CoerceSelectedOperationType(object? target, Type? selectedOperationType)
    {
        return _runtime.CoerceSelectedOperationType(_catalog, target, selectedOperationType);
    }

    /// <summary>
    /// Creates a runtime operation instance for the selected operation type.
    /// </summary>
    public Operation? CreateOperation(Type? operationType)
    {
        return _runtime.CreateOperation(operationType);
    }

    /// <summary>
    /// Resolves the operation identifier that should remain selected after the target changes by first keeping the
    /// current compatible selection and then falling back to the remembered operation for the new target type.
    /// </summary>
    public OperationId? ResolveSelectedOperationId(
        IReadOnlyList<OperationDescriptor> availableOperations,
        OperationId? currentSelectedOperationId,
        OperationId? rememberedOperationId)
    {
        if (availableOperations == null)
        {
            throw new ArgumentNullException(nameof(availableOperations));
        }

        if (currentSelectedOperationId != null && availableOperations.Any(descriptor => descriptor.Id == currentSelectedOperationId.Value))
        {
            return currentSelectedOperationId;
        }

        if (rememberedOperationId != null && availableOperations.Any(descriptor => descriptor.Id == rememberedOperationId.Value))
        {
            return rememberedOperationId;
        }

        return null;
    }

    /// <summary>
    /// Returns the required option set types for the selected operation and target.
    /// </summary>
    public IReadOnlyList<Type> GetEnabledOptionSetTypes(Operation? operation, IOperationTarget? target)
    {
        return _runtime.GetRequiredOptionSetTypes(operation, target);
    }

    /// <summary>
    /// Returns the current execution blocking reason for the selected operation and parameter state.
    /// </summary>
    public string? GetExecuteDisabledReason(Operation? operation, OperationParameters parameters)
    {
        return _runtime.CheckRequirements(operation, parameters);
    }

    /// <summary>
    /// Returns whether the selected operation is currently runnable.
    /// </summary>
    public bool CanExecute(Operation? operation, OperationParameters parameters)
    {
        return GetExecuteDisabledReason(operation, parameters) == null;
    }

    /// <summary>
    /// Returns the command preview text for the selected operation and parameter state.
    /// </summary>
    public string GetVisibleCommandText(Operation? operation, OperationParameters parameters)
    {
        return _runtime.GetVisibleCommandText(operation, parameters);
    }

    /// <summary>
    /// Returns the first command preview line for clipboard copy actions.
    /// </summary>
    public string? GetPrimaryCommandText(Operation? operation, OperationParameters parameters)
    {
        return _runtime.GetPrimaryCommandText(operation, parameters);
    }

    /// <summary>
    /// Builds the previewable execution plan for the selected operation and parameter state when one exists.
    /// </summary>
    public ExecutionPlan? GetExecutionPlan(Operation? operation, OperationParameters parameters)
    {
        /* Trace plan-preview construction at the application-service boundary so shells can distinguish the end-to-end
           refresh cost from the runtime cost of building the underlying execution plan. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("GetExecutionPlan");
        PerformanceTelemetry.SetTag(activity, "operation.present", operation != null);
        PerformanceTelemetry.SetTag(activity, "operation.type", operation?.GetType().Name ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "operation.name", operation?.OperationName ?? string.Empty);
        PerformanceTelemetry.SetTag(activity, "target.type", parameters.Target?.GetType().Name ?? string.Empty);

        ExecutionPlan? plan = _runtime.BuildExecutionPlan(operation, parameters);
        PerformanceTelemetry.SetTag(activity, "plan.has_result", plan != null);
        PerformanceTelemetry.SetTag(activity, "plan.task.count", plan?.Tasks.Count ?? 0);
        return plan;
    }
}
