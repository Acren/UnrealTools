using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace LocalAutomation.Application;

/// <summary>
/// Wraps an extension-owned runtime parameter object so UI hosts can edit targets, additional arguments, and option
/// sets without referencing the extension's concrete parameter types.
/// </summary>
public sealed class OperationParameterSession : INotifyPropertyChanged
{
    private readonly Extensions.Abstractions.IOperationAdapter _adapter;
    private readonly INotifyPropertyChanged? _observableParameters;

    /// <summary>
    /// Creates a parameter session around the provided adapter-owned runtime parameter object.
    /// </summary>
    public OperationParameterSession(Extensions.Abstractions.IOperationAdapter adapter, object rawValue)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        RawValue = rawValue ?? throw new ArgumentNullException(nameof(rawValue));
        _observableParameters = rawValue as INotifyPropertyChanged;
        if (_observableParameters != null)
        {
            _observableParameters.PropertyChanged += HandleUnderlyingParametersChanged;
        }
    }

    /// <summary>
    /// Raised when the wrapped parameter object reports that one of its visible values changed.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the wrapped runtime parameter object used by execution and option persistence services.
    /// </summary>
    public object RawValue { get; }

    /// <summary>
    /// Gets or sets the currently selected runtime target.
    /// </summary>
    public object? Target
    {
        get => _adapter.GetTarget(RawValue);
        set => _adapter.SetTarget(RawValue, value);
    }

    /// <summary>
    /// Gets or sets the freeform additional-arguments value.
    /// </summary>
    public string AdditionalArguments
    {
        get => _adapter.GetAdditionalArguments(RawValue);
        set => _adapter.SetAdditionalArguments(RawValue, value);
    }

    /// <summary>
    /// Gets the current live option-set instances.
    /// </summary>
    public IReadOnlyList<object> OptionSets => _adapter.GetOptionSets(RawValue);

    /// <summary>
    /// Ensures the provided option-set type exists and returns the live instance.
    /// </summary>
    public object EnsureOptionSet(Type optionSetType)
    {
        return _adapter.EnsureOptionSet(RawValue, optionSetType);
    }

    /// <summary>
    /// Removes the provided option-set type when it is currently present.
    /// </summary>
    public bool RemoveOptionSet(Type optionSetType)
    {
        return _adapter.RemoveOptionSet(RawValue, optionSetType);
    }

    /// <summary>
    /// Clears all option-set instances from the wrapped runtime parameter object.
    /// </summary>
    public void ResetOptionSets()
    {
        _adapter.ResetOptionSets(RawValue);
    }

    /// <summary>
    /// Returns the display name for the provided option-set instance.
    /// </summary>
    public string GetOptionSetName(object optionSet)
    {
        return _adapter.GetOptionSetName(optionSet);
    }

    /// <summary>
    /// Mirrors property-changed notifications from the wrapped runtime parameter object.
    /// </summary>
    private void HandleUnderlyingParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
    }
}
