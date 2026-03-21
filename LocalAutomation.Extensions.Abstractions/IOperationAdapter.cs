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
    /// Creates a runtime parameter container compatible with this adapter's operations.
    /// </summary>
    object CreateParameters();

    /// <summary>
    /// Assigns the current target onto the provided runtime parameter container.
    /// </summary>
    void SetTarget(object parameters, object? target);

    /// <summary>
    /// Gets the current target from the provided runtime parameter container.
    /// </summary>
    object? GetTarget(object parameters);

    /// <summary>
    /// Gets the current freeform additional-arguments value from the provided runtime parameter container.
    /// </summary>
    string GetAdditionalArguments(object parameters);

    /// <summary>
    /// Updates the freeform additional-arguments value on the provided runtime parameter container.
    /// </summary>
    void SetAdditionalArguments(object parameters, string additionalArguments);

    /// <summary>
    /// Gets the live option-set instances currently stored on the provided runtime parameter container.
    /// </summary>
    IReadOnlyList<object> GetOptionSets(object parameters);

    /// <summary>
    /// Ensures an option set of the provided runtime type exists on the parameter container and returns the live instance.
    /// </summary>
    object EnsureOptionSet(object parameters, Type optionSetType);

    /// <summary>
    /// Removes the option set of the provided runtime type from the parameter container when present.
    /// </summary>
    bool RemoveOptionSet(object parameters, Type optionSetType);

    /// <summary>
    /// Clears all live option-set instances from the provided runtime parameter container.
    /// </summary>
    void ResetOptionSets(object parameters);

    /// <summary>
    /// Gets the user-facing display name for the provided runtime option-set instance.
    /// </summary>
    string GetOptionSetName(object optionSet);

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
