using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts a single operation option property into a generic editable field for the Avalonia parity shell.
/// </summary>
public sealed class OptionFieldViewModel : ViewModelBase
{
    private readonly PropertyInfo _property;
    private readonly OperationOptions _owner;

    /// <summary>
    /// Creates a field view model around a single public option property.
    /// </summary>
    public OptionFieldViewModel(OperationOptions owner, PropertyInfo property)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _property = property ?? throw new ArgumentNullException(nameof(property));

        if (IsEnum)
        {
            foreach (object value in Enum.GetValues(GetValueType()))
            {
                EnumChoices.Add(new EnumChoiceViewModel(value, value.ToString()!.SplitWordsByUppercase()));
            }
        }
    }

    /// <summary>
    /// Gets the display name shown in the editor for this field.
    /// </summary>
    public string DisplayName => _property.Name.SplitWordsByUppercase();

    /// <summary>
    /// Gets whether the field renders as a boolean toggle.
    /// </summary>
    public bool IsBoolean => GetValueType() == typeof(bool);

    /// <summary>
    /// Gets whether the field renders as a free-form text box.
    /// </summary>
    public bool IsString => GetValueType() == typeof(string);

    /// <summary>
    /// Gets whether the field renders as an enum-backed choice list.
    /// </summary>
    public bool IsEnum => GetValueType().IsEnum;

    /// <summary>
    /// Gets whether the current generic editor can render this field.
    /// </summary>
    public bool IsSupported => IsBoolean || IsString || IsEnum;

    /// <summary>
    /// Gets the available enum choices when the field is enum-backed.
    /// </summary>
    public ObservableCollection<EnumChoiceViewModel> EnumChoices { get; } = new();

    /// <summary>
    /// Gets or sets the current boolean value.
    /// </summary>
    public bool BoolValue
    {
        get => (bool?)GetValue() ?? false;
        set
        {
            if (BoolValue != value)
            {
                SetValue(value);
                RaiseFieldChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the current string value.
    /// </summary>
    public string StringValue
    {
        get => (string?)GetValue() ?? string.Empty;
        set
        {
            if (!string.Equals(StringValue, value, StringComparison.Ordinal))
            {
                SetValue(value);
                RaiseFieldChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the currently selected enum choice.
    /// </summary>
    public EnumChoiceViewModel? SelectedEnumChoice
    {
        get
        {
            object? currentValue = GetValue();
            if (currentValue == null)
            {
                return null;
            }

            return EnumChoices.FirstOrDefault(choice => Equals(choice.Value, currentValue));
        }
        set
        {
            if (value != null && !Equals(GetValue(), value.Value))
            {
                SetValue(value.Value);
                RaiseFieldChanged();
            }
        }
    }

    /// <summary>
    /// Refreshes derived property notifications from the underlying model.
    /// </summary>
    public void RefreshFromModel()
    {
        RaiseFieldChanged();
    }

    /// <summary>
    /// Gets the actual value type for the property, unwrapping Option&lt;T&gt; wrappers when necessary.
    /// </summary>
    private Type GetValueType()
    {
        Type propertyType = _property.PropertyType;
        if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Option<>))
        {
            return propertyType.GetGenericArguments()[0];
        }

        return propertyType;
    }

    /// <summary>
    /// Reads the current field value from the owning option set.
    /// </summary>
    private object? GetValue()
    {
        object? propertyValue = _property.GetValue(_owner);
        if (propertyValue == null)
        {
            return null;
        }

        Type propertyType = _property.PropertyType;
        if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Option<>))
        {
            return propertyType.GetProperty(nameof(Option<bool>.Value))?.GetValue(propertyValue);
        }

        return propertyValue;
    }

    /// <summary>
    /// Writes a value back to the owning option set, unwrapping Option&lt;T&gt; wrappers when required.
    /// </summary>
    private void SetValue(object value)
    {
        object? propertyValue = _property.GetValue(_owner);
        Type propertyType = _property.PropertyType;
        if (propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(Option<>))
        {
            propertyType.GetProperty(nameof(Option<bool>.Value))?.SetValue(propertyValue, value);
            return;
        }

        _property.SetValue(_owner, value);
    }

    /// <summary>
    /// Raises derived property notifications after the underlying value changes.
    /// </summary>
    private void RaiseFieldChanged()
    {
        RaisePropertyChanged(nameof(BoolValue));
        RaisePropertyChanged(nameof(StringValue));
        RaisePropertyChanged(nameof(SelectedEnumChoice));
    }
}
