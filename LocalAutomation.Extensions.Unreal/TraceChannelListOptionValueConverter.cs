using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using Newtonsoft.Json.Linq;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Persists trace-channel selections as stable channel keys instead of live binding-list objects.
/// </summary>
public sealed class TraceChannelListOptionValueConverter : IOptionValueConverter
{
    /// <summary>
    /// Gets the stable converter identifier.
    /// </summary>
    public string Id => "unreal.option-values.trace-channel-list";

    /// <summary>
    /// Returns whether the provided value type is a binding list of trace channels.
    /// </summary>
    public bool CanConvert(Type valueType)
    {
        return valueType.IsGenericType
               && valueType.GetGenericTypeDefinition() == typeof(BindingList<>)
               && valueType.GetGenericArguments()[0] == typeof(TraceChannel);
    }

    /// <summary>
    /// Converts trace channels into their stable keys.
    /// </summary>
    public object? Serialize(Type valueType, object? value)
    {
        return value is IEnumerable<TraceChannel> channels
            ? channels.Select(channel => channel.Key).ToList()
            : new List<string>();
    }

    /// <summary>
    /// Rehydrates a trace-channel binding list from persisted channel keys.
    /// </summary>
    public object? Deserialize(Type valueType, object? persistedValue)
    {
        IEnumerable<string> keys = persistedValue switch
        {
            JArray jsonArray => jsonArray.Values<string>().Where(item => !string.IsNullOrWhiteSpace(item))!,
            IEnumerable<string> rawStrings => rawStrings.Where(item => !string.IsNullOrWhiteSpace(item)),
            _ => Array.Empty<string>()
        };

        BindingList<TraceChannel> restoredChannels = new();
        foreach (string key in keys)
        {
            TraceChannel? channel = TraceChannels.Channels.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (channel != null)
            {
                restoredChannels.Add(channel);
            }
        }

        return restoredChannels;
    }
}
