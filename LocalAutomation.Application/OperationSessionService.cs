using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Application;

/// <summary>
/// Coordinates operation selection and derived UI state so desktop shells can consume a shared application model
/// instead of recomputing the same operation rules inside window code.
/// </summary>
public sealed class OperationSessionService
{
    private readonly OperationCatalogService _catalog;

    /// <summary>
    /// Creates an operation session service from the shared catalog.
    /// </summary>
    public OperationSessionService(OperationCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Creates a host-facing parameter session around the shared runtime parameter model.
    /// </summary>
    public OperationParameterSession CreateParameterSession()
    {
        return new OperationParameterSession(new OperationParameters());
    }

    /// <summary>
    /// Ensures the selected operation type remains compatible with the current target selection.
    /// </summary>
    public Type? CoerceSelectedOperationType(object? target, Type? selectedOperationType)
    {
        if (target == null)
        {
            return null;
        }

        IReadOnlyList<Type> availableOperationTypes = _catalog.GetAvailableOperationTypes(target);
        if (availableOperationTypes.Count == 0)
        {
            return null;
        }

        if (selectedOperationType != null && availableOperationTypes.Contains(selectedOperationType))
        {
            return selectedOperationType;
        }

        return availableOperationTypes[0];
    }

    /// <summary>
    /// Creates a runtime operation instance for the selected operation type.
    /// </summary>
    public Operation? CreateOperation(Type? operationType)
    {
        return operationType == null ? null : Operation.CreateOperation(operationType);
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
        if (operation == null || target == null)
        {
            return Array.Empty<Type>();
        }

        return operation.GetRequiredOptionSetTypes(target).ToList();
    }

    /// <summary>
    /// Returns the current execution blocking reason for the selected operation and parameter state.
    /// </summary>
    public string? GetExecuteDisabledReason(Operation? operation, OperationParameters parameters)
    {
        if (operation == null)
        {
            return "No operation selected";
        }

        try
        {
            return operation.CheckRequirementsSatisfied(parameters);
        }
        catch (Exception ex)
        {
            LogOperationSessionFailure(operation.GetType(), "validate execution requirements", ex);
            return "Operation validation failed. See the application log for details.";
        }
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
        if (operation == null)
        {
            return "No operation";
        }

        try
        {
            IReadOnlyList<string> commandTexts = operation.GetCommandTexts(parameters);
            return commandTexts.Count > 0 ? string.Join("\n", commandTexts) : "No command";
        }
        catch (Exception ex)
        {
            LogOperationSessionFailure(operation.GetType(), "build command preview", ex);
            return "Command preview failed. See the application log for details.";
        }
    }

    /// <summary>
    /// Returns the first command preview line for clipboard copy actions.
    /// </summary>
    public string? GetPrimaryCommandText(Operation? operation, OperationParameters parameters)
    {
        if (operation == null)
        {
            return null;
        }

        try
        {
            return operation.GetCommandTexts(parameters).FirstOrDefault();
        }
        catch (Exception ex)
        {
            LogOperationSessionFailure(operation.GetType(), "build the primary command preview", ex);
            return null;
        }
    }

    /// <summary>
    /// Builds the previewable execution plan for the selected operation and parameter state when one exists.
    /// </summary>
    public LocalAutomation.Runtime.ExecutionPlan? GetExecutionPlan(Operation? operation, OperationParameters parameters)
    {
        /* Trace plan-preview construction at the application-service boundary so shells can distinguish the end-to-end
           refresh cost from the runtime cost of building the underlying execution plan. */
        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("GetExecutionPlan")
            .SetTag("operation.present", operation != null)
            .SetTag("operation.type", operation?.GetType().Name ?? string.Empty)
            .SetTag("operation.name", operation?.OperationName ?? string.Empty)
            .SetTag("target.type", parameters.Target?.GetType().Name ?? string.Empty);

        LocalAutomation.Runtime.ExecutionPlan? plan;
        if (operation == null)
        {
            plan = null;
        }
        else
        {
            try
            {
                plan = ExecutionPlanFactory.BuildPlan(operation, parameters);
            }
            catch (Exception ex)
            {
                LogOperationSessionFailure(operation.GetType(), "build execution plan", ex);
                plan = null;
            }
        }

        activity.SetTag("plan.has_result", plan != null)
            .SetTag("plan.task.count", plan?.Tasks.Count ?? 0);
        return plan;
    }

    /// <summary>
    /// Logs operation inspection failures when the application logger is available, while still allowing tests or
    /// startup paths to proceed before logging is initialized.
    /// </summary>
    private static void LogOperationSessionFailure(Type operationType, string activity, Exception ex)
    {
        try
        {
            ApplicationLogger.Logger.LogError(ex, "Operation session failed to {Activity} for '{OperationType}'.", activity, operationType.FullName ?? operationType.Name);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
