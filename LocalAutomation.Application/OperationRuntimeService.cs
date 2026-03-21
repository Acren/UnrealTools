using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using Microsoft.Extensions.Logging;

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
    /// Creates a host-facing parameter session around the shared runtime parameter model contributed by the registered
    /// operation adapter.
    /// </summary>
    public OperationParameterSession CreateParameterSession()
    {
        IOperationAdapter adapter = GetDefaultAdapter();
        return new OperationParameterSession(adapter, adapter.CreateParameters());
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

        try
        {
            return GetAdapter(operation.GetType()).SupportsMultipleEngines(operation);
        }
        catch (Exception ex)
        {
            LogOperationRuntimeFailure(operation.GetType(), "check whether the operation supports multiple engines", ex);
            return false;
        }
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

        try
        {
            return GetAdapter(operation.GetType()).GetRequiredOptionSetTypes(operation, target);
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
    public string? CheckRequirements(object? operation, object parameters)
    {
        if (operation == null)
        {
            return "No operation selected";
        }

        try
        {
            return GetAdapter(operation.GetType()).CheckRequirements(operation, parameters);
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
    public string GetVisibleCommandText(object? operation, object parameters)
    {
        if (operation == null)
        {
            return "No operation";
        }

        try
        {
            IReadOnlyList<string> commandTexts = GetAdapter(operation.GetType()).GetCommandTexts(operation, parameters);
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
    public string? GetPrimaryCommandText(object? operation, object parameters)
    {
        if (operation == null)
        {
            return null;
        }

        try
        {
            return GetAdapter(operation.GetType()).GetCommandTexts(operation, parameters).FirstOrDefault();
        }
        catch (Exception ex)
        {
            LogOperationRuntimeFailure(operation.GetType(), "build the primary command preview", ex);
            return null;
        }
    }

    /// <summary>
    /// Returns the display name for the provided runtime option-set instance through its owning adapter.
    /// </summary>
    public string GetOptionSetName(object optionSet)
    {
        if (optionSet == null)
        {
            throw new ArgumentNullException(nameof(optionSet));
        }

        return GetDefaultAdapter().GetOptionSetName(optionSet);
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

    /// <summary>
    /// Returns the single shared operation adapter used by the current bridge-era shell parameter model.
    /// </summary>
    private IOperationAdapter GetDefaultAdapter()
    {
        IOperationAdapter? adapter = _catalog.OperationAdapters.FirstOrDefault();
        if (adapter == null)
        {
            return new EmptyOperationAdapter();
        }

        return adapter;
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
