using System;
using System.Collections.Generic;
using System.ComponentModel;
using LocalAutomation.Core;
using LocalAutomation.Runtime;

namespace LocalAutomation.Application;

/// <summary>
/// Wraps the shared runtime parameter object so UI hosts can edit targets, additional arguments, and option sets
/// without depending on extension-specific parameter subclasses.
/// </summary>
public sealed class OperationParameterSession : INotifyPropertyChanged
{
    private OperationParameters _parameters;

    /// <summary>
    /// Creates a parameter session around the provided shared runtime parameter object.
    /// </summary>
    public OperationParameterSession(OperationParameters parameters)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _parameters.PropertyChanged += HandleUnderlyingParametersChanged;
    }

    /// <summary>
    /// Raised when the wrapped parameter object reports that one of its visible values changed.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets the wrapped runtime parameter object used by execution and option persistence services.
    /// </summary>
    public OperationParameters RawValue => _parameters;

    /// <summary>
    /// Replaces the wrapped runtime parameter object while preserving the outer session instance observed by the UI.
    /// </summary>
    public void Replace(OperationParameters parameters)
    {
        Replace(parameters, parameters?.Target);
    }

    /// <summary>
    /// Replaces the wrapped runtime parameter object while explicitly applying the active target so callers can keep
    /// target selection stable across operation-specific parameter recreation.
    /// </summary>
    public void Replace(OperationParameters parameters, IOperationTarget? target)
    {
        using OperationSwitchActivityScope activity = OperationSwitchTelemetry.StartActivity("ReplaceParameters");
        OperationSwitchTelemetry.SetTag(activity, "incoming_option_set.count", parameters?.OptionsInstances.Count ?? 0);
        OperationSwitchTelemetry.SetTag(activity, "target.type", target?.GetType().Name ?? string.Empty);

        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }

        parameters.Target = target;

        if (ReferenceEquals(_parameters, parameters))
        {
            return;
        }

        _parameters.PropertyChanged -= HandleUnderlyingParametersChanged;
        _parameters = parameters;
        _parameters.PropertyChanged += HandleUnderlyingParametersChanged;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RawValue)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Target)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AdditionalArguments)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OptionSets)));

        OperationSwitchTelemetry.SetTag(activity, "final_option_set.count", _parameters.OptionsInstances.Count);
    }

    /// <summary>
    /// Gets or sets the currently selected runtime target.
    /// </summary>
    public IOperationTarget? Target
    {
        get => _parameters.Target;
        set => _parameters.Target = value;
    }

    /// <summary>
    /// Gets or sets the freeform additional-arguments value.
    /// </summary>
    public string AdditionalArguments
    {
        get => _parameters.AdditionalArguments;
        set => _parameters.AdditionalArguments = value;
    }

    /// <summary>
    /// Gets the current live option-set instances.
    /// </summary>
    public IReadOnlyList<OperationOptions> OptionSets => _parameters.OptionsInstances;

    /// <summary>
    /// Ensures the provided option-set type exists and returns the live instance.
    /// </summary>
    public OperationOptions EnsureOptionSet(Type optionSetType)
    {
        return _parameters.EnsureOptionsInstance(optionSetType);
    }

    /// <summary>
    /// Removes the provided option-set type when it is currently present.
    /// </summary>
    public bool RemoveOptionSet(Type optionSetType)
    {
        return _parameters.RemoveOptionsInstance(optionSetType);
    }

    /// <summary>
    /// Clears all option-set instances from the wrapped runtime parameter object.
    /// </summary>
    public void ResetOptionSets()
    {
        _parameters.ResetOptions();
    }

    /// <summary>
    /// Returns the display name for the provided option-set instance.
    /// </summary>
    public string GetOptionSetName(OperationOptions optionSet)
    {
        return optionSet.Name;
    }

    /// <summary>
    /// Mirrors property-changed notifications from the wrapped runtime parameter object.
    /// </summary>
    private void HandleUnderlyingParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
    }
}
