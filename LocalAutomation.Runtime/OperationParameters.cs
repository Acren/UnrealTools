using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Handles retry callbacks for operations that need host intervention after a failure.
/// </summary>
public delegate void RetryHandler(Exception ex);

/// <summary>
/// Stores the mutable runtime state for an operation instance.
/// </summary>
public class OperationParameters : INotifyPropertyChanged
{
    private BindingList<OperationOptions> _optionsInstances;
    private IOperationTarget? _target;

    /// <summary>
    /// Creates an empty parameter set with no option groups.
    /// </summary>
    public OperationParameters()
    {
        _optionsInstances = new BindingList<OperationOptions>();
        OptionsInstances = _optionsInstances;
    }

    /// <summary>
    /// Raised whenever parameter state changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets the host callback used to retry failed work.
    /// </summary>
    [JsonIgnore]
    public RetryHandler? RetryHandler { get; set; }

    /// <summary>
    /// Gets or sets an explicit output path override for the operation.
    /// </summary>
    [JsonIgnore]
    public string? OutputPathOverride { get; set; }

    /// <summary>
    /// Gets or sets the live option instances attached to this parameter object.
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public BindingList<OperationOptions> OptionsInstances
    {
        get => _optionsInstances;
        set
        {
            System.Collections.Generic.List<OperationOptions> initialOptions = value.ToList();
            initialOptions.Sort();
            _optionsInstances = new BindingList<OperationOptions>(initialOptions);
            _optionsInstances.ListChanged += (_, _) =>
            {
                RefreshOptionsSubscriptions();
                OnPropertyChanged(nameof(OptionsInstances));
                OnOptionsStateChanged();
            };

            RefreshOptionsSubscriptions();
            UpdateOptionsTarget();
        }
    }

    /// <summary>
    /// Gets or sets the current operation target.
    /// </summary>
    public IOperationTarget? Target
    {
        get => _target;
        set
        {
            if (_target == value)
            {
                return;
            }

            if (_target != null)
            {
                _target.PropertyChanged -= TargetChanged;
            }

            _target = value;
            if (_target != null)
            {
                _target.PropertyChanged += TargetChanged;
            }

            UpdateOptionsTarget();
            OnPropertyChanged();
            OnTargetStateChanged();
        }
    }

    /// <summary>
    /// Returns the live option-set instance for the provided runtime type.
    /// </summary>
    public OperationOptions? GetOptionsInstance(Type optionsType)
    {
        foreach (OperationOptions options in OptionsInstances)
        {
            if (options.GetType() == optionsType)
            {
                return options;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the live option-set instance for the provided runtime type, creating it when it has not yet been
    /// materialized for the current parameter object.
    /// </summary>
    public T GetOptions<T>() where T : OperationOptions
    {
        Type optionsType = typeof(T);
        T? existingOptions = GetOptionsInstance(optionsType) as T;
        if (existingOptions != null)
        {
            return existingOptions;
        }

        T newOptions = (T)Activator.CreateInstance(optionsType)!;
        SetOptions(newOptions);
        return newOptions;
    }

    /// <summary>
     /// Ensures a live option-set instance exists for the provided runtime type.
     /// </summary>
    public OperationOptions EnsureOptionsInstance(Type optionsType)
    {
        OperationOptions? existingOptions = GetOptionsInstance(optionsType);
        if (existingOptions != null)
        {
            return existingOptions;
        }

        OperationOptions newOptions = (OperationOptions)Activator.CreateInstance(optionsType)!;
        SetOptions(newOptions);
        return newOptions;
    }

    /// <summary>
    /// Adds a new option set to the parameter object in its sorted position.
    /// </summary>
    public void SetOptions<T>(T options) where T : OperationOptions
    {
        if (GetOptionsInstance(typeof(T)) != null)
        {
            throw new Exception("Parameters already has options of this type");
        }

        int desiredIndex = 0;
        foreach (OperationOptions optionsInstance in OptionsInstances)
        {
            if (options.CompareTo(optionsInstance) < 0)
            {
                break;
            }

            desiredIndex++;
        }

        options.OperationTarget = Target;
        OptionsInstances.Insert(desiredIndex, options);
    }

    /// <summary>
    /// Removes the live option-set instance for the provided runtime type when present.
    /// </summary>
    public bool RemoveOptionsInstance(Type optionsType)
    {
        OperationOptions? options = GetOptionsInstance(optionsType);
        if (options == null)
        {
            return false;
        }

        return OptionsInstances.Remove(options);
    }

    /// <summary>
    /// Clears all option sets from the parameter object.
    /// </summary>
    public void ResetOptions()
    {
        OptionsInstances.Clear();
    }

    /// <summary>
    /// Creates a new parameter object of the same runtime type and copies the current generic state into it.
    /// </summary>
    public virtual OperationParameters CreateChild()
    {
        OperationParameters child = (OperationParameters)Activator.CreateInstance(GetType())!;
        child.Target = Target;
        child.OutputPathOverride = OutputPathOverride;

        foreach (OperationOptions options in OptionsInstances)
        {
            child.SetOptions((OperationOptions)options.Clone());
        }

        return child;
    }

    /// <summary>
    /// Raises the property-changed event for the provided property.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Lets subclasses react when target-dependent option state changes.
    /// </summary>
    protected virtual void OnOptionsStateChanged()
    {
    }

    /// <summary>
    /// Lets subclasses react when the current target changes.
    /// </summary>
    protected virtual void OnTargetStateChanged()
    {
    }

    /// <summary>
    /// Rebinds nested option-set listeners whenever the option list changes.
    /// </summary>
    private void RefreshOptionsSubscriptions()
    {
        foreach (OperationOptions options in _optionsInstances)
        {
            options.PropertyChanged -= HandleOptionsInstancePropertyChanged;
            options.PropertyChanged += HandleOptionsInstancePropertyChanged;
        }
    }

    /// <summary>
    /// Propagates nested option changes up to the parameter container.
    /// </summary>
    private void HandleOptionsInstancePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(OptionsInstances));
        OnOptionsStateChanged();
    }

    /// <summary>
    /// Updates each live option set so it points at the current target.
    /// </summary>
    private void UpdateOptionsTarget()
    {
        foreach (OperationOptions options in OptionsInstances.ToList())
        {
            options.OperationTarget = Target;
        }
    }

    /// <summary>
    /// Propagates target property changes to the parameter object.
    /// </summary>
    private void TargetChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(Target));
    }
}
