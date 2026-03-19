using System;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Converts special option property value types to and from stable persisted representations so hosts can save option
/// values without serializing live runtime objects.
/// </summary>
public interface IOptionValueConverter
{
    /// <summary>
    /// Gets the stable converter identifier used for diagnostics.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Returns whether this converter can handle the provided option property value type.
    /// </summary>
    bool CanConvert(Type valueType);

    /// <summary>
    /// Converts a live option property value into a stable persisted representation.
    /// </summary>
    object? Serialize(Type valueType, object? value);

    /// <summary>
    /// Rehydrates a persisted value into the runtime type used by the option property.
    /// </summary>
    object? Deserialize(Type valueType, object? persistedValue);
}
