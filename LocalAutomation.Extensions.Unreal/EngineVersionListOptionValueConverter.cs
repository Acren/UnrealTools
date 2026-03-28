using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using Newtonsoft.Json.Linq;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Persists engine-version lists as stable version strings instead of live runtime objects.
/// </summary>
public sealed class EngineVersionListOptionValueConverter : IOptionValueConverter
{
    /// <summary>
    /// Gets the stable converter identifier.
    /// </summary>
    public string Id => "unreal.option-values.engine-version-list";

    /// <summary>
     /// Returns whether the provided value type is a list of engine versions.
     /// </summary>
    public bool CanConvert(Type valueType)
    {
        return valueType.IsArray && valueType.GetElementType() == typeof(EngineVersion)
               || valueType.IsGenericType
               && (valueType.GetGenericTypeDefinition() == typeof(List<>)
                   || valueType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
               && valueType.GetGenericArguments()[0] == typeof(EngineVersion);
    }

    /// <summary>
    /// Converts engine versions into version-string tokens.
    /// </summary>
    public object? Serialize(Type valueType, object? value)
    {
        return value is IEnumerable<EngineVersion> versions
            ? versions.Select(version => version.ToString()).ToList()
            : new List<string>();
    }

    /// <summary>
    /// Rehydrates engine versions from persisted version strings.
    /// </summary>
    public object? Deserialize(Type valueType, object? persistedValue)
    {
        IEnumerable<string?> versionStrings = persistedValue switch
        {
            JArray jsonArray => jsonArray.Values<string>(),
            IEnumerable<string> rawStrings => rawStrings,
            _ => Array.Empty<string?>()
        };

        return ToSelectionList(versionStrings
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static version => new EngineVersion(version!)));
    }

    /// <summary>
    /// Materializes the restored engine versions into the immutable selection shape the runtime option model exposes.
     /// </summary>
    private static IReadOnlyList<EngineVersion> ToSelectionList(IEnumerable<EngineVersion> versions)
    {
        return versions.ToArray();
    }
}
