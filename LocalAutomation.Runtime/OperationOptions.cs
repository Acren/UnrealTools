using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides a base observable wrapper for option values.
/// </summary>
public class Option : INotifyPropertyChanged
{
    /// <summary>
    /// Raised whenever the wrapped option value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the property-changed event for the provided property.
    /// </summary>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Wraps a typed option value so host property grids can treat it as an observable object.
/// </summary>
public class Option<T> : Option
{
    private T _value;

    /// <summary>
    /// Creates a typed option with the provided default value.
    /// </summary>
    public Option(T defaultValue)
    {
        _value = defaultValue;
    }

    /// <summary>
    /// Creates a typed option and invokes a callback whenever the value changes.
    /// </summary>
    public Option(Action changedCallback, T defaultValue)
    {
        _value = defaultValue;
        PropertyChanged += (_, _) => changedCallback();
    }

    /// <summary>
    /// Gets or sets the wrapped value.
    /// </summary>
    public T Value
    {
        get => _value;
        set
        {
            _value = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Reads the wrapped value directly from the option wrapper.
    /// </summary>
    public static implicit operator T(Option<T> option)
    {
        return option.Value;
    }

    /// <summary>
    /// Wraps a raw value into an observable option instance.
    /// </summary>
    public static implicit operator Option<T>(T value)
    {
        return new Option<T>(value);
    }
}

/// <summary>
/// Provides the shared runtime model for a configurable option set.
/// </summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public class OperationOptions : INotifyPropertyChanged, IComparable<OperationOptions>
{
    private IOperationTarget? _operationTarget;

    /// <summary>
    /// Subscribes to nested option wrappers so value changes bubble up through the containing option set.
    /// </summary>
    public OperationOptions()
    {
        PropertyInfo[] properties = GetType().GetProperties();
        foreach (PropertyInfo property in properties)
        {
            if (!property.PropertyType.IsSubclassOf(typeof(Option)))
            {
                continue;
            }

            if (property.GetValue(this) is Option option)
            {
                option.PropertyChanged += (_, _) => OptionChanged();
            }
        }
    }

    /// <summary>
    /// Gets the sort index used to order option groups in the UI.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public virtual int SortIndex => 0;

    /// <summary>
    /// Gets the default user-facing name for the option group.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public virtual string Name
    {
        get
        {
            string name = GetType().Name.Replace("Options", string.Empty);
            return SplitWordsByUppercase(name);
        }
    }

    /// <summary>
    /// Gets or sets the target currently associated with this option set.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public IOperationTarget? OperationTarget
    {
        get => _operationTarget;
        set
        {
            _operationTarget = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Raised whenever the option set changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Creates a shallow clone of the option set for legacy callers that still work with copies.
    /// </summary>
    public OperationOptions Clone()
    {
        return (OperationOptions)MemberwiseClone();
    }

    /// <summary>
    /// Raises a property-changed notification for the option set itself.
    /// </summary>
    protected void OptionChanged()
    {
        OnPropertyChanged();
    }

    /// <summary>
    /// Raises the property-changed event for the provided property.
    /// </summary>
    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Orders option sets first by sort index and then by display name.
    /// </summary>
    public int CompareTo(OperationOptions? other)
    {
        if (other == null)
        {
            return 1;
        }

        if (SortIndex != other.SortIndex)
        {
            return SortIndex.CompareTo(other.SortIndex);
        }

        return string.Compare(Name, other.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Expands PascalCase identifiers into space-separated words for UI labels.
    /// </summary>
    private static string SplitWordsByUppercase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        System.Text.StringBuilder builder = new();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 && char.IsUpper(current))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
