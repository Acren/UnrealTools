using System;
using System.Collections.Generic;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Provides a no-op parameter model when the host has not loaded any operation adapters yet so the generic shell can
/// still start and show extension-load diagnostics instead of crashing during startup.
/// </summary>
internal sealed class EmptyOperationAdapter : IOperationAdapter
{
    private sealed class EmptyParameters
    {
        public object? Target { get; set; }
    }

    /// <summary>
    /// Gets the stable identifier for the no-op adapter.
    /// </summary>
    public string Id => "host.empty-operation-adapter";

    /// <summary>
    /// Returns false because the no-op adapter never handles real operation types.
    /// </summary>
    public bool CanHandle(Type operationType)
    {
        return false;
    }

    /// <summary>
    /// Throws because real operations cannot be created without a loaded extension.
    /// </summary>
    public object CreateOperation(Type operationType)
    {
        throw new InvalidOperationException("No operation adapter is loaded.");
    }

    /// <summary>
    /// Creates an empty parameter container for extension-less startup.
    /// </summary>
    public object CreateParameters()
    {
        return new EmptyParameters();
    }

    /// <summary>
    /// Stores the current selected target on the empty parameter container.
    /// </summary>
    public void SetTarget(object parameters, object? target)
    {
        ((EmptyParameters)parameters).Target = target;
    }

    /// <summary>
    /// Returns the current selected target from the empty parameter container.
    /// </summary>
    public object? GetTarget(object parameters)
    {
        return ((EmptyParameters)parameters).Target;
    }

    /// <summary>
    /// Returns an empty additional-arguments value because no extension-specific parameter model is loaded.
    /// </summary>
    public string GetAdditionalArguments(object parameters)
    {
        return string.Empty;
    }

    /// <summary>
    /// Ignores additional-arguments writes because no extension-specific parameter model is loaded.
    /// </summary>
    public void SetAdditionalArguments(object parameters, string additionalArguments)
    {
    }

    /// <summary>
    /// Returns no option sets because no extension-specific parameter model is loaded.
    /// </summary>
    public IReadOnlyList<object> GetOptionSets(object parameters)
    {
        return Array.Empty<object>();
    }

    /// <summary>
    /// Throws because no real option sets can be created without a loaded extension.
    /// </summary>
    public object EnsureOptionSet(object parameters, Type optionSetType)
    {
        throw new InvalidOperationException("No operation adapter is loaded.");
    }

    /// <summary>
    /// Returns false because there are no option sets to remove.
    /// </summary>
    public bool RemoveOptionSet(object parameters, Type optionSetType)
    {
        return false;
    }

    /// <summary>
    /// Clears nothing because there are no option sets.
    /// </summary>
    public void ResetOptionSets(object parameters)
    {
    }

    /// <summary>
    /// Returns the runtime type name for diagnostics when no extension-specific display name is available.
    /// </summary>
    public string GetOptionSetName(object optionSet)
    {
        return optionSet.GetType().Name;
    }

    /// <summary>
    /// Returns no required option sets because no real operation is loaded.
    /// </summary>
    public IReadOnlyList<Type> GetRequiredOptionSetTypes(object operation, object? target)
    {
        return Array.Empty<Type>();
    }

    /// <summary>
    /// Returns false because no real operation is loaded.
    /// </summary>
    public bool SupportsMultipleEngines(object operation)
    {
        return false;
    }

    /// <summary>
    /// Returns a clear disabled reason for empty-extension startup scenarios.
    /// </summary>
    public string? CheckRequirements(object operation, object parameters)
    {
        return "No extension loaded.";
    }

    /// <summary>
    /// Returns no command preview because no real operation is loaded.
    /// </summary>
    public IReadOnlyList<string> GetCommandTexts(object operation, object parameters)
    {
        return Array.Empty<string>();
    }
}
