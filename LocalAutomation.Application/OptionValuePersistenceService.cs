using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using LocalAutomation.Extensions.Abstractions;
using Newtonsoft.Json.Linq;

namespace LocalAutomation.Application;

/// <summary>
/// Extracts and reapplies stable option property values so UI hosts can persist target-scoped configuration without
/// serializing live runtime option objects directly.
/// </summary>
public sealed class OptionValuePersistenceService
{
    private readonly ExtensionCatalog _catalog;

    /// <summary>
    /// Creates an option value persistence service around the shared extension catalog.
    /// </summary>
    public OptionValuePersistenceService(ExtensionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Captures stable option values for the provided runtime option sets.
    /// </summary>
    public Dictionary<string, JToken> Capture(IEnumerable<object> optionSets)
    {
        Dictionary<string, JToken> values = new(StringComparer.Ordinal);

        foreach (object optionSet in optionSets)
        {
            foreach (PropertyInfo property in GetPersistableProperties(optionSet.GetType()))
            {
                Type valueType = property.PropertyType;
                object? value = property.GetValue(optionSet);
                if (TryUnwrapOptionValue(property.PropertyType, value, out Type? wrappedValueType, out object? wrappedValue))
                {
                    valueType = wrappedValueType!;
                    value = wrappedValue;
                }

                if (!TrySerializeValue(valueType, value, out JToken? token))
                {
                    continue;
                }

                values[BuildPropertyKey(optionSet.GetType(), property.Name)] = token;
            }
        }

        return values;
    }

    /// <summary>
    /// Applies persisted values back onto the provided runtime option sets.
    /// </summary>
    public void Apply(IEnumerable<object> optionSets, IReadOnlyDictionary<string, JToken>? values)
    {
        if (values == null || values.Count == 0)
        {
            return;
        }

        foreach (object optionSet in optionSets)
        {
            foreach (PropertyInfo property in GetPersistableProperties(optionSet.GetType()))
            {
                string propertyKey = BuildPropertyKey(optionSet.GetType(), property.Name);
                if (!values.TryGetValue(propertyKey, out JToken? token))
                {
                    continue;
                }

                object? propertyValue = property.GetValue(optionSet);
                Type valueType = property.PropertyType;
                if (TryUnwrapOptionValue(property.PropertyType, propertyValue, out Type? wrappedValueType, out _))
                {
                    valueType = wrappedValueType!;
                }

                if (!TryDeserializeValue(valueType, token, out object? restoredValue))
                {
                    continue;
                }

                if (IsOptionWrapperType(property.PropertyType) && propertyValue != null)
                {
                    PropertyInfo? wrappedValueProperty = property.PropertyType.GetProperty("Value");
                    wrappedValueProperty?.SetValue(propertyValue, restoredValue);
                    continue;
                }

                if (property.CanWrite)
                {
                    property.SetValue(optionSet, restoredValue);
                    continue;
                }

                ApplyToExistingCollection(propertyValue, restoredValue);
            }
        }
    }

    /// <summary>
    /// Returns the visible, editable properties that should participate in stable persistence.
    /// </summary>
    private static IEnumerable<PropertyInfo> GetPersistableProperties(Type optionSetType)
    {
        return optionSetType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead)
            .Where(property => property.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false);
    }

    /// <summary>
    /// Builds a stable key for the option property.
    /// </summary>
    private static string BuildPropertyKey(Type optionSetType, string propertyName)
    {
        return $"{optionSetType.FullName}.{propertyName}";
    }

    /// <summary>
    /// Detects and unwraps the runtime option wrapper used by the existing Unreal option model.
    /// </summary>
    private static bool TryUnwrapOptionValue(Type propertyType, object? propertyValue, out Type? wrappedValueType, out object? wrappedValue)
    {
        if (!IsOptionWrapperType(propertyType))
        {
            wrappedValueType = null;
            wrappedValue = null;
            return false;
        }

        PropertyInfo? valueProperty = propertyType.GetProperty("Value");
        wrappedValueType = valueProperty?.PropertyType;
        wrappedValue = propertyValue == null ? null : valueProperty?.GetValue(propertyValue);
        return wrappedValueType != null;
    }

    /// <summary>
    /// Returns whether the provided property type is the existing generic option wrapper.
    /// </summary>
    private static bool IsOptionWrapperType(Type propertyType)
    {
        return propertyType.IsGenericType && string.Equals(propertyType.Name, "Option`1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Converts a runtime option value into a persisted token.
    /// </summary>
    private bool TrySerializeValue(Type valueType, object? value, out JToken? token)
    {
        IOptionValueConverter? converter = _catalog.OptionValueConverters.FirstOrDefault(item => item.CanConvert(valueType));
        object? serializableValue = converter != null ? converter.Serialize(valueType, value) : SerializeSimpleValue(valueType, value);
        if (serializableValue == null && value != null)
        {
            token = null;
            return false;
        }

        token = JToken.FromObject(serializableValue ?? JValue.CreateNull());
        return true;
    }

    /// <summary>
    /// Restores a runtime option value from a persisted token.
    /// </summary>
    private bool TryDeserializeValue(Type valueType, JToken token, out object? value)
    {
        IOptionValueConverter? converter = _catalog.OptionValueConverters.FirstOrDefault(item => item.CanConvert(valueType));
        if (converter != null)
        {
            value = converter.Deserialize(valueType, token);
            return true;
        }

        return TryDeserializeSimpleValue(valueType, token, out value);
    }

    /// <summary>
    /// Serializes common primitive and collection shapes automatically.
    /// </summary>
    private static object? SerializeSimpleValue(Type valueType, object? value)
    {
        if (value == null)
        {
            return null;
        }

        Type nonNullableType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (nonNullableType.IsEnum)
        {
            return value.ToString();
        }

        if (nonNullableType == typeof(string) || nonNullableType == typeof(bool) || nonNullableType.IsPrimitive || nonNullableType == typeof(decimal))
        {
            return value;
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            List<object?> items = new();
            foreach (object? item in enumerable)
            {
                items.Add(item);
            }

            return items;
        }

        return null;
    }

    /// <summary>
    /// Restores common primitive and collection shapes automatically.
    /// </summary>
    private static bool TryDeserializeSimpleValue(Type valueType, JToken token, out object? value)
    {
        Type nonNullableType = Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (token.Type == JTokenType.Null)
        {
            value = null;
            return true;
        }

        if (nonNullableType.IsEnum)
        {
            value = Enum.Parse(nonNullableType, token.Value<string>()!, ignoreCase: true);
            return true;
        }

        if (nonNullableType == typeof(string))
        {
            value = token.Value<string>() ?? string.Empty;
            return true;
        }

        if (nonNullableType == typeof(bool))
        {
            value = token.Value<bool>();
            return true;
        }

        if (nonNullableType.IsPrimitive || nonNullableType == typeof(decimal))
        {
            value = token.ToObject(nonNullableType, Newtonsoft.Json.JsonSerializer.CreateDefault());
            return true;
        }

        if (typeof(IList).IsAssignableFrom(nonNullableType))
        {
            value = token.ToObject(nonNullableType, Newtonsoft.Json.JsonSerializer.CreateDefault());
            return value != null;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Applies restored list values into an existing read-only collection property.
    /// </summary>
    private static void ApplyToExistingCollection(object? existingValue, object? restoredValue)
    {
        if (existingValue is not IList existingList || restoredValue is not IEnumerable restoredEnumerable)
        {
            return;
        }

        existingList.Clear();
        foreach (object? item in restoredEnumerable)
        {
            existingList.Add(item);
        }
    }
}
