using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Resolves extension-provided runtime adapters so hosts can create and inspect operation instances without knowing
/// their concrete framework-specific base classes.
/// </summary>
public sealed class OperationRuntimeService
{
    private readonly ExtensionCatalog _catalog;

    /// <summary>
    /// Creates an operation runtime service around the shared extension catalog.
    /// </summary>
    public OperationRuntimeService(ExtensionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Creates a runtime operation instance for the provided operation type.
    /// </summary>
    public object? CreateOperation(Type? operationType)
    {
        if (operationType == null)
        {
            return null;
        }

        return GetAdapter(operationType).CreateOperation(operationType);
    }

    /// <summary>
    /// Returns whether the provided operation instance supports multiple engines.
    /// </summary>
    public bool SupportsMultipleEngines(object? operation)
    {
        if (operation == null)
        {
            return false;
        }

        return GetAdapter(operation.GetType()).SupportsMultipleEngines(operation);
    }

    /// <summary>
    /// Returns the option set types required by the provided operation and target combination.
    /// </summary>
    public IReadOnlyList<Type> GetRequiredOptionSetTypes(object? operation, object? target)
    {
        if (operation == null)
        {
            return Array.Empty<Type>();
        }

        return GetAdapter(operation.GetType()).GetRequiredOptionSetTypes(operation, target);
    }

    /// <summary>
    /// Returns the current validation error for the provided operation and parameter state, or <c>null</c> when the
    /// operation can execute.
    /// </summary>
    public string? CheckRequirements(object? operation, object parameters)
    {
        if (operation == null)
        {
            return "No operation selected";
        }

        return GetAdapter(operation.GetType()).CheckRequirements(operation, parameters);
    }

    /// <summary>
    /// Formats the visible command preview text for the provided operation and parameter state.
    /// </summary>
    public string GetVisibleCommandText(object? operation, object parameters)
    {
        if (operation == null)
        {
            return "No operation";
        }

        IReadOnlyList<string> commandTexts = GetAdapter(operation.GetType()).GetCommandTexts(operation, parameters);
        return commandTexts.Count > 0 ? string.Join("\n", commandTexts) : "No command";
    }

    /// <summary>
    /// Returns the first formatted command preview line for clipboard-style actions.
    /// </summary>
    public string? GetPrimaryCommandText(object? operation, object parameters)
    {
        if (operation == null)
        {
            return null;
        }

        return GetAdapter(operation.GetType()).GetCommandTexts(operation, parameters).FirstOrDefault();
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
    /// Finds the registered adapter responsible for the provided operation type.
    /// </summary>
    private IOperationAdapter GetAdapter(Type operationType)
    {
        foreach (IOperationAdapter adapter in _catalog.OperationAdapters)
        {
            if (adapter.CanHandle(operationType))
            {
                return adapter;
            }
        }

        throw new InvalidOperationException($"No registered operation adapter can handle '{operationType.FullName}'.");
    }
}
