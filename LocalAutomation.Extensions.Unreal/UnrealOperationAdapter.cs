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
    /// Creates a new Unreal parameter container for bridge-era hosts.
    /// </summary>
    public object CreateParameters()
    {
        return new OperationParameters();
    }

    /// <summary>
    /// Assigns the current runtime target to the provided Unreal parameter container.
    /// </summary>
    public void SetTarget(object parameters, object? target)
    {
        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        typedParameters.Target = target as IOperationTarget;
    }

    /// <summary>
    /// Returns the current runtime target from the provided Unreal parameter container.
    /// </summary>
    public object? GetTarget(object parameters)
    {
        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        return typedParameters.Target;
    }

    /// <summary>
    /// Returns the current additional-arguments value from the provided Unreal parameter container.
    /// </summary>
    public string GetAdditionalArguments(object parameters)
    {
        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        return typedParameters.AdditionalArguments;
    }

    /// <summary>
    /// Updates the additional-arguments value on the provided Unreal parameter container.
    /// </summary>
    public void SetAdditionalArguments(object parameters, string additionalArguments)
    {
        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        typedParameters.AdditionalArguments = additionalArguments ?? string.Empty;
    }

    /// <summary>
    /// Returns the live Unreal option instances stored on the provided parameter container.
    /// </summary>
    public IReadOnlyList<object> GetOptionSets(object parameters)
    {
        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        return typedParameters.OptionsInstances.Cast<object>().ToList();
    }

    /// <summary>
    /// Ensures the requested Unreal option set exists and returns the live instance.
    /// </summary>
    public object EnsureOptionSet(object parameters, Type optionSetType)
    {
        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        return typedParameters.EnsureOptionsInstance(optionSetType);
    }

    /// <summary>
    /// Removes the requested Unreal option set when it is currently present.
    /// </summary>
    public bool RemoveOptionSet(object parameters, Type optionSetType)
    {
        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        return typedParameters.RemoveOptionsInstance(optionSetType);
    }

    /// <summary>
    /// Clears all live Unreal option instances from the provided parameter container.
    /// </summary>
    public void ResetOptionSets(object parameters)
    {
        if (parameters is not OperationParameters typedParameters)
        {
            throw new ArgumentException("Parameters are not Unreal operation parameters", nameof(parameters));
        }

        typedParameters.ResetOptions();
    }

    /// <summary>
    /// Returns the display name already exposed by the Unreal option model.
    /// </summary>
    public string GetOptionSetName(object optionSet)
    {
        if (optionSet is not OperationOptions typedOptions)
        {
            throw new ArgumentException("Option set is not a supported Unreal option model", nameof(optionSet));
        }

        return typedOptions.Name;
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

        List<Type> optionTypes = typedOperation.GetRequiredOptionSetTypes(typedTarget).ToList();

        // Keep freeform passthrough arguments available through the same option pipeline as the rest of the Unreal
        // configuration without requiring the generic shell to know about this legacy option type.
        if (!optionTypes.Contains(typeof(UnrealAutomationCommon.Operations.OperationOptionTypes.AdditionalArgumentsOptions)))
        {
            optionTypes.Add(typeof(UnrealAutomationCommon.Operations.OperationOptionTypes.AdditionalArgumentsOptions));
        }

        return optionTypes;
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
