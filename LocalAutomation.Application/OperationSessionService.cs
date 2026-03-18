using System;
using System.Collections.Generic;

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
    public object? CreateOperation(Type? operationType)
    {
        return _runtime.CreateOperation(operationType);
    }

    /// <summary>
    /// Returns whether the selected operation supports multiple engine selections.
    /// </summary>
    public bool SupportsMultipleEngines(object? operation)
    {
        return _runtime.SupportsMultipleEngines(operation);
    }

    /// <summary>
    /// Returns the required option set types for the selected operation and target.
    /// </summary>
    public IReadOnlyList<Type> GetEnabledOptionSetTypes(object? operation, object? target)
    {
        return _runtime.GetRequiredOptionSetTypes(operation, target);
    }

    /// <summary>
    /// Returns the current execution blocking reason for the selected operation and parameter state.
    /// </summary>
    public string? GetExecuteDisabledReason(object? operation, object parameters)
    {
        return _runtime.CheckRequirements(operation, parameters);
    }

    /// <summary>
    /// Returns whether the selected operation is currently runnable.
    /// </summary>
    public bool CanExecute(object? operation, object parameters)
    {
        return GetExecuteDisabledReason(operation, parameters) == null;
    }

    /// <summary>
    /// Returns the command preview text for the selected operation and parameter state.
    /// </summary>
    public string GetVisibleCommandText(object? operation, object parameters)
    {
        return _runtime.GetVisibleCommandText(operation, parameters);
    }

    /// <summary>
    /// Returns the first command preview line for clipboard copy actions.
    /// </summary>
    public string? GetPrimaryCommandText(object? operation, object parameters)
    {
        return _runtime.GetPrimaryCommandText(operation, parameters);
    }
}
