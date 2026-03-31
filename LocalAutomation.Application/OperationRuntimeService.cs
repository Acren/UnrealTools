using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Core;
using LocalAutomation.Runtime;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Application;

/// <summary>
/// Resolves canonical runtime operations so hosts can create and inspect operations without routing behavior through
/// extension-owned adapter seams.
/// </summary>
public sealed class OperationRuntimeService
{
    /// <summary>
    /// Creates a runtime operation instance for the provided operation type.
    /// </summary>
    public Operation? CreateOperation(Type? operationType)
    {
        if (operationType == null)
        {
            return null;
        }

        return Operation.CreateOperation(operationType);
    }

    /// <summary>
    /// Creates a host-facing parameter session around the shared runtime parameter model.
    /// </summary>
    public OperationParameterSession CreateParameterSession()
    {
        return new OperationParameterSession(new OperationParameters());
    }

    /// <summary>
    /// Returns the option set types required by the provided operation and target combination.
    /// </summary>
    public IReadOnlyList<Type> GetRequiredOptionSetTypes(Operation? operation, IOperationTarget? target)
    {
        if (operation == null)
        {
            return Array.Empty<Type>();
        }

        try
        {
            if (target == null)
            {
                return Array.Empty<Type>();
            }

            using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("GetRequiredOptionSetTypes");
            PerformanceTelemetry.SetTag(activity, "operation.type", operation.GetType().Name);
            PerformanceTelemetry.SetTag(activity, "target.type", target.GetType().Name);

            IReadOnlyList<Type> optionTypes = operation.GetRequiredOptionSetTypes(target).ToList();
            PerformanceTelemetry.SetTag(activity, "count", optionTypes.Count);
            return optionTypes;
        }
        catch (Exception ex)
        {
            LogOperationRuntimeFailure(operation.GetType(), "compute required option sets", ex);
            return Array.Empty<Type>();
        }
    }

    /// <summary>
    /// Returns the current validation error for the provided operation and parameter state, or <c>null</c> when the
    /// operation can execute.
    /// </summary>
    public string? CheckRequirements(Operation? operation, OperationParameters parameters)
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
            LogOperationRuntimeFailure(operation.GetType(), "validate execution requirements", ex);
            return "Operation validation failed. See the application log for details.";
        }
    }

    /// <summary>
    /// Formats the visible command preview text for the provided operation and parameter state.
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
            LogOperationRuntimeFailure(operation.GetType(), "build command preview", ex);
            return "Command preview failed. See the application log for details.";
        }
    }

    /// <summary>
    /// Returns the first formatted command preview line for clipboard-style actions.
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
            LogOperationRuntimeFailure(operation.GetType(), "build the primary command preview", ex);
            return null;
        }
    }

    /// <summary>
    /// Builds the previewable execution plan for the provided operation and parameter state when the operation can
    /// describe one.
    /// </summary>
    public LocalAutomation.Runtime.ExecutionPlan? BuildExecutionPlan(Operation? operation, OperationParameters parameters)
    {
        if (operation == null)
        {
            return null;
        }

        try
        {
            return operation.BuildExecutionPlan(parameters);
        }
        catch (Exception ex)
        {
            LogOperationRuntimeFailure(operation.GetType(), "build execution plan", ex);
            return null;
        }
    }

    /// <summary>
    /// Returns the display name for the provided runtime option-set instance.
    /// </summary>
    public string GetOptionSetName(OperationOptions optionSet)
    {
        if (optionSet == null)
        {
            throw new ArgumentNullException(nameof(optionSet));
        }

        return optionSet.Name;
    }

    /// <summary>
    /// Ensures the currently selected operation type is valid for the target, falling back to the first compatible
    /// registered type when needed.
    /// </summary>
    public Type? CoerceSelectedOperationType(OperationCatalogService catalog, object? target, Type? selectedOperationType)
    {
        if (catalog == null)
        {
            throw new ArgumentNullException(nameof(catalog));
        }

        IReadOnlyList<Type> availableOperationTypes = catalog.GetAvailableOperationTypes(target);
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
    /// Logs extension runtime failures when the application logger is available, while still allowing tests or
    /// extension-less startup to proceed before logging is initialized.
    /// </summary>
    private static void LogOperationRuntimeFailure(Type operationType, string activity, Exception ex)
    {
        try
        {
            ApplicationLogger.Logger.LogError(ex, "Operation runtime failed to {Activity} for '{OperationType}'.", activity, operationType.FullName ?? operationType.Name);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
