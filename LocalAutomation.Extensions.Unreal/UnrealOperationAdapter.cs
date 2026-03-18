using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Bridges the existing UnrealAutomationCommon operation runtime behavior into the LocalAutomation application layer.
/// </summary>
public sealed class UnrealOperationAdapter : IOperationAdapter
{
    /// <summary>
    /// Gets the stable identifier for the Unreal runtime adapter.
    /// </summary>
    public string Id => "unreal.operation-adapter";

    /// <summary>
    /// Returns whether the provided type is backed by the current Unreal operation hierarchy.
    /// </summary>
    public bool CanHandle(Type operationType)
    {
        return typeof(Operation).IsAssignableFrom(operationType);
    }

    /// <summary>
    /// Creates a new Unreal operation instance from the provided type.
    /// </summary>
    public object CreateOperation(Type operationType)
    {
        return Operation.CreateOperation(operationType);
    }

    /// <summary>
    /// Returns the option set types required for the provided Unreal operation and target.
    /// </summary>
    public IReadOnlyList<Type> GetRequiredOptionSetTypes(object operation, object? target)
    {
        if (operation is not Operation typedOperation || target is not IOperationTarget typedTarget)
        {
            return Array.Empty<Type>();
        }

        return typedOperation.GetRequiredOptionSetTypes(typedTarget).ToList();
    }

    /// <summary>
    /// Returns whether the provided Unreal operation supports multiple engine selections.
    /// </summary>
    public bool SupportsMultipleEngines(object operation)
    {
        return operation is Operation typedOperation && typedOperation.SupportsMultipleEngines;
    }

    /// <summary>
    /// Returns the current execution blocking reason for the provided Unreal operation parameters.
    /// </summary>
    public string? CheckRequirements(object operation, object parameters)
    {
        if (operation is not Operation typedOperation || parameters is not OperationParameters typedParameters)
        {
            return "Operation parameters are incompatible";
        }

        return typedOperation.CheckRequirementsSatisfied(typedParameters);
    }

    /// <summary>
    /// Returns formatted command preview strings for the provided Unreal operation and parameter state.
    /// </summary>
    public IReadOnlyList<string> GetCommandTexts(object operation, object parameters)
    {
        if (operation is not Operation typedOperation || parameters is not OperationParameters typedParameters)
        {
            return Array.Empty<string>();
        }

        return typedOperation.GetCommands(typedParameters).Select(command => command.ToString()).ToList();
    }
}
