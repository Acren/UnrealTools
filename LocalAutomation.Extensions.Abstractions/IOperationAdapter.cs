using System;
using System.Collections.Generic;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Bridges extension-specific operation runtime behavior into the generic application layer without forcing that
/// layer to reference product-specific operation base classes.
/// </summary>
public interface IOperationAdapter
{
    /// <summary>
    /// Gets the stable identifier for this adapter.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Returns whether this adapter can create and inspect the provided runtime operation type.
    /// </summary>
    bool CanHandle(Type operationType);

    /// <summary>
    /// Creates a runtime operation instance for the provided type.
    /// </summary>
    object CreateOperation(Type operationType);

    /// <summary>
    /// Returns the option set types required by the operation for the provided target.
    /// </summary>
    IReadOnlyList<Type> GetRequiredOptionSetTypes(object operation, object? target);

    /// <summary>
    /// Returns whether the operation supports multiple engines.
    /// </summary>
    bool SupportsMultipleEngines(object operation);

    /// <summary>
    /// Returns the current execution blocking reason, or <c>null</c> when the operation is runnable.
    /// </summary>
    string? CheckRequirements(object operation, object parameters);

    /// <summary>
    /// Returns the formatted command preview strings for the current operation and parameter state.
    /// </summary>
    IReadOnlyList<string> GetCommandTexts(object operation, object parameters);
}
