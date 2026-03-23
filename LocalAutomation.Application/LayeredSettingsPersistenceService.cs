using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using LocalAutomation.Application.Diagnostics;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Persistence;
using LocalAutomation.Runtime;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LocalAutomation.Application;

/// <summary>
/// Resolves, applies, and writes layered persisted values for option sets and target-scoped settings.
/// </summary>
public sealed class LayeredSettingsPersistenceService
{
    private readonly ExtensionCatalog _catalog;
    private readonly string _globalSettingsFilePath;
    private readonly string _targetSettingsFileName;
    private readonly string _userTargetSettingsDirectoryPath;
    private readonly Dictionary<(Type OwnerType, string Prefix), IReadOnlyList<PersistedSettingDescriptor>> _descriptorCache = new();
    private readonly Dictionary<(Type OwnerType, string TargetTypeId), string> _optionSetPrefixCache = new();
    private readonly Dictionary<(Type OwnerType, string TargetTypeId), string> _targetSettingsPrefixCache = new();
    private readonly Dictionary<Type, string> _typeOwnerPrefixCache = new();

    /// <summary>
    /// Creates a layered settings service rooted in the current branded LocalAppData location.
    /// </summary>
    public LayeredSettingsPersistenceService(ExtensionCatalog catalog, string appDataRootPath, string targetSettingsFileName)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        if (string.IsNullOrWhiteSpace(appDataRootPath))
        {
            throw new ArgumentException("App data root path must be provided.", nameof(appDataRootPath));
        }

        _globalSettingsFilePath = Path.Combine(appDataRootPath, "global-settings.json");
        _targetSettingsFileName = string.IsNullOrWhiteSpace(targetSettingsFileName)
            ? throw new ArgumentException("Target settings file name must be provided.", nameof(targetSettingsFileName))
            : targetSettingsFileName;
        _userTargetSettingsDirectoryPath = Path.Combine(appDataRootPath, "target-overrides");
        ValidateGeneratedKeys();
    }

    /// <summary>
    /// Creates the persistence context for one runtime target.
    /// </summary>
    public TargetSettingsContext CreateTargetContext(TargetDiscoveryService targets, IOperationTarget target)
    {
        if (targets == null)
        {
            throw new ArgumentNullException(nameof(targets));
        }

        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        TargetTypeId targetTypeId = targets.GetTargetTypeId(target) ?? throw new InvalidOperationException($"No target descriptor matched '{target.GetType().Name}'.");
        string targetPath = targets.GetTargetPath(target);
        TargetKey targetKey = BuildTargetKey(targetTypeId, targetPath);
        return new TargetSettingsContext(target, targetTypeId, targetPath, targetKey);
    }

    /// <summary>
    /// Builds the stable appdata key used for one target's override file.
    /// </summary>
    public TargetKey BuildTargetKey(TargetTypeId targetTypeId, string targetPath)
    {
        return TargetKeyUtility.BuildTargetKey(targetTypeId, targetPath);
    }

    /// <summary>
    /// Builds the stable appdata key used for one target's override file from serialized values.
    /// </summary>
    public string BuildTargetKey(string targetTypeId, string targetPath)
    {
        return BuildTargetKey(new TargetTypeId(targetTypeId), targetPath).Value;
    }

    /// <summary>
    /// Loads layered values and applies the effective result to the provided option sets.
    /// </summary>
    public void ApplyOptionValues(IEnumerable<object> optionSets, TargetSettingsContext? context)
    {
        if (context == null)
        {
            return;
        }

        List<object> optionSetList = optionSets.ToList();
        using Activity? activity = OperationSwitchTelemetry.StartActivity("ApplyOptionValues");
        OperationSwitchTelemetry.SetTag(activity, "target.type", context.TargetTypeId.Value);
        OperationSwitchTelemetry.SetTag(activity, "count", optionSetList.Count);

        IReadOnlyDictionary<string, JToken> resolvedValues = ResolveEffectiveValues(context);
        int descriptorCount = 0;
        foreach (object optionSet in optionSetList)
        {
            IReadOnlyList<PersistedSettingDescriptor> descriptors = GetOptionSetDescriptors(optionSet.GetType(), context.TargetTypeId);
            descriptorCount += descriptors.Count;
            ApplyResolvedValues(optionSet, descriptors, resolvedValues);
        }

        OperationSwitchTelemetry.SetTag(activity, "resolved_value.count", resolvedValues.Count);
        OperationSwitchTelemetry.SetTag(activity, "descriptor.count", descriptorCount);
    }

    /// <summary>
    /// Loads layered values and applies the effective result to the provided target settings object.
    /// </summary>
    public void ApplyTargetSettings(object settingsOwner, TargetSettingsContext? context)
    {
        if (context == null || settingsOwner == null)
        {
            return;
        }

        IReadOnlyDictionary<string, JToken> resolvedValues = ResolveEffectiveValues(context);
        ApplyResolvedValues(settingsOwner, GetTargetSettingsDescriptors(settingsOwner.GetType(), context.TargetTypeId), resolvedValues);
    }

    /// <summary>
    /// Writes the current option-set values to each property's configured write layer.
    /// </summary>
    public void SaveOptionValues(IEnumerable<object> optionSets, TargetSettingsContext? context)
    {
        if (context == null)
        {
            return;
        }

        PersistedSettingValueCollection globalValues = LoadCollection(_globalSettingsFilePath);
        PersistedSettingValueCollection targetLocalValues = LoadCollection(GetTargetLocalSettingsFilePath(context));
        PersistedSettingValueCollection userOverrideValues = LoadCollection(GetUserTargetOverrideFilePath(context));

        foreach (object optionSet in optionSets)
        {
            foreach (PersistedSettingDescriptor descriptor in GetOptionSetDescriptors(optionSet.GetType(), context.TargetTypeId))
            {
                if (!TryReadValueToken(optionSet, descriptor, out JToken? token))
                {
                    continue;
                }

                WriteDescriptorValue(descriptor, token!, globalValues, targetLocalValues, userOverrideValues);
            }
        }

        SaveCollections(context, globalValues, targetLocalValues, userOverrideValues);
    }

    /// <summary>
    /// Writes the current target-settings values to each property's configured write layer.
    /// </summary>
    public void SaveTargetSettings(object settingsOwner, TargetSettingsContext? context)
    {
        if (context == null || settingsOwner == null)
        {
            return;
        }

        PersistedSettingValueCollection globalValues = LoadCollection(_globalSettingsFilePath);
        PersistedSettingValueCollection targetLocalValues = LoadCollection(GetTargetLocalSettingsFilePath(context));
        PersistedSettingValueCollection userOverrideValues = LoadCollection(GetUserTargetOverrideFilePath(context));

        foreach (PersistedSettingDescriptor descriptor in GetTargetSettingsDescriptors(settingsOwner.GetType(), context.TargetTypeId))
        {
            if (!TryReadValueToken(settingsOwner, descriptor, out JToken? token))
            {
                continue;
            }

            WriteDescriptorValue(descriptor, token!, globalValues, targetLocalValues, userOverrideValues);
        }

        SaveCollections(context, globalValues, targetLocalValues, userOverrideValues);
    }

    /// <summary>
    /// Returns the current target-local settings file path for the provided target context.
    /// </summary>
    public string GetTargetLocalSettingsFilePath(TargetSettingsContext context)
    {
        return Path.Combine(context.Target.TargetDirectory, _targetSettingsFileName);
    }

    /// <summary>
    /// Returns the current user override settings file path for the provided target context.
    /// </summary>
    public string GetUserTargetOverrideFilePath(TargetSettingsContext context)
    {
        string fileName = SanitizeFileName(context.TargetKey.Value) + ".json";
        return Path.Combine(_userTargetSettingsDirectoryPath, fileName);
    }

    /// <summary>
    /// Loads the three participating layers and merges them into one effective value map.
    /// </summary>
    private IReadOnlyDictionary<string, JToken> ResolveEffectiveValues(TargetSettingsContext context)
    {
        using Activity? activity = OperationSwitchTelemetry.StartActivity("ResolveEffectiveValues");
        OperationSwitchTelemetry.SetTag(activity, "target.type", context.TargetTypeId.Value);

        PersistedSettingValueCollection globalValues = LoadCollection(_globalSettingsFilePath, "Global");
        PersistedSettingValueCollection targetLocalValues = LoadCollection(GetTargetLocalSettingsFilePath(context), "TargetLocal");
        PersistedSettingValueCollection userOverrideValues = LoadCollection(GetUserTargetOverrideFilePath(context), "UserOverride");

        Dictionary<string, JToken> values = new(StringComparer.Ordinal);
        OverlayValues(values, globalValues);
        OverlayValues(values, targetLocalValues);
        OverlayValues(values, userOverrideValues);

        OperationSwitchTelemetry.SetTag(activity, "global_value.count", globalValues.Values.Count);
        OperationSwitchTelemetry.SetTag(activity, "target_local_value.count", targetLocalValues.Values.Count);
        OperationSwitchTelemetry.SetTag(activity, "user_override_value.count", userOverrideValues.Values.Count);
        OperationSwitchTelemetry.SetTag(activity, "resolved_value.count", values.Count);
        return values;
    }

    /// <summary>
    /// Overlays one persisted value collection onto the effective result map.
    /// </summary>
    private static void OverlayValues(IDictionary<string, JToken> destination, PersistedSettingValueCollection source)
    {
        foreach ((string key, JToken value) in source.Values)
        {
            destination[key] = value.DeepClone();
        }
    }

    /// <summary>
    /// Applies already-resolved values to one reflected settings owner.
    /// </summary>
    private void ApplyResolvedValues(object owner, IReadOnlyList<PersistedSettingDescriptor> descriptors, IReadOnlyDictionary<string, JToken> resolvedValues)
    {
        foreach (PersistedSettingDescriptor descriptor in descriptors)
        {
            if (!resolvedValues.TryGetValue(descriptor.Key, out JToken? token))
            {
                continue;
            }

            if (!TryDeserializeValue(descriptor.ValueType, token, out object? restoredValue))
            {
                continue;
            }

            object? propertyValue = descriptor.Property.GetValue(owner);
            if (descriptor.IsOptionWrapper && propertyValue != null)
            {
                PropertyInfo? wrappedValueProperty = descriptor.Property.PropertyType.GetProperty("Value");
                wrappedValueProperty?.SetValue(propertyValue, restoredValue);
                continue;
            }

            if (descriptor.Property.CanWrite)
            {
                descriptor.Property.SetValue(owner, restoredValue);
                continue;
            }

            ApplyToExistingCollection(propertyValue, restoredValue);
        }
    }

    /// <summary>
    /// Writes one reflected value into the appropriate configured storage layer.
    /// </summary>
    private static void WriteDescriptorValue(
        PersistedSettingDescriptor descriptor,
        JToken token,
        PersistedSettingValueCollection globalValues,
        PersistedSettingValueCollection targetLocalValues,
        PersistedSettingValueCollection userOverrideValues)
    {
        PersistedSettingValueCollection destination = descriptor.WriteScope switch
        {
            PersistenceScope.Global => globalValues,
            PersistenceScope.TargetLocal => targetLocalValues,
            _ => userOverrideValues
        };

        destination.Values[descriptor.Key] = token.DeepClone();
    }

    /// <summary>
    /// Saves the participating layer files back to disk.
    /// </summary>
    private void SaveCollections(
        TargetSettingsContext context,
        PersistedSettingValueCollection globalValues,
        PersistedSettingValueCollection targetLocalValues,
        PersistedSettingValueCollection userOverrideValues)
    {
        SaveCollection(_globalSettingsFilePath, globalValues);
        SaveCollection(GetTargetLocalSettingsFilePath(context), targetLocalValues);
        SaveCollection(GetUserTargetOverrideFilePath(context), userOverrideValues);
    }

    /// <summary>
    /// Loads one persisted value collection from disk.
    /// </summary>
    private static PersistedSettingValueCollection LoadCollection(string filePath)
    {
        return LoadCollection(filePath, string.Empty);
    }

    /// <summary>
    /// Loads one persisted value collection from disk while recording a tracing span for the storage layer involved.
    /// </summary>
    private static PersistedSettingValueCollection LoadCollection(string filePath, string layerName)
    {
        using Activity? activity = OperationSwitchTelemetry.StartActivity("LoadSettingsFile");
        bool fileExists = File.Exists(filePath);
        OperationSwitchTelemetry.SetTag(activity, "layer.name", layerName);
        OperationSwitchTelemetry.SetTag(activity, "file.exists", fileExists);

        if (fileExists)
        {
            try
            {
                OperationSwitchTelemetry.SetTag(activity, "file.length", new FileInfo(filePath).Length);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        JsonFileStateStore<PersistedSettingValueCollection> store = new(
            filePath: filePath,
            createDefaultState: static () => new PersistedSettingValueCollection(),
            createSerializer: static () => new JsonSerializer());

        PersistedSettingValueCollection state = store.Load().State;
        OperationSwitchTelemetry.SetTag(activity, "value.count", state.Values.Count);
        return state;
    }

    /// <summary>
    /// Saves one persisted value collection to disk.
    /// </summary>
    private static void SaveCollection(string filePath, PersistedSettingValueCollection state)
    {
        JsonFileStateStore<PersistedSettingValueCollection> store = new(
            filePath: filePath,
            createDefaultState: static () => new PersistedSettingValueCollection(),
            createSerializer: static () => new JsonSerializer());

        store.Save(state);
    }

    /// <summary>
    /// Returns the descriptors for one option-set type and caches the reflected metadata.
    /// </summary>
    private IReadOnlyList<PersistedSettingDescriptor> GetOptionSetDescriptors(Type optionSetType, TargetTypeId targetTypeId)
    {
        string ownerPrefix = GetOptionSetOwnerPrefix(optionSetType, targetTypeId);
        return GetOrCreateDescriptors(optionSetType, ownerPrefix, () => BuildDescriptors(optionSetType, ownerPrefix));
    }

    /// <summary>
    /// Returns the descriptors for one target-settings type and caches the reflected metadata.
    /// </summary>
    private IReadOnlyList<PersistedSettingDescriptor> GetTargetSettingsDescriptors(Type settingsType, TargetTypeId targetTypeId)
    {
        string ownerPrefix = GetTargetSettingsOwnerPrefix(settingsType, targetTypeId);
        return GetOrCreateDescriptors(settingsType, ownerPrefix, () => BuildDescriptors(settingsType, ownerPrefix));
    }

    /// <summary>
    /// Returns cached descriptors for a type or creates them on first use.
    /// </summary>
    private IReadOnlyList<PersistedSettingDescriptor> GetOrCreateDescriptors(Type ownerType, string ownerPrefix, Func<IReadOnlyList<PersistedSettingDescriptor>> createDescriptors)
    {
        using Activity? activity = OperationSwitchTelemetry.StartActivity("ResolveDescriptors");
        OperationSwitchTelemetry.SetTag(activity, "owner.type", ownerType.FullName ?? ownerType.Name);
        OperationSwitchTelemetry.SetTag(activity, "owner.prefix", ownerPrefix);

        if (_descriptorCache.TryGetValue((ownerType, ownerPrefix), out IReadOnlyList<PersistedSettingDescriptor>? cachedDescriptors))
        {
            OperationSwitchTelemetry.SetTag(activity, "cache.hit", true);
            OperationSwitchTelemetry.SetTag(activity, "descriptor.count", cachedDescriptors.Count);
            return cachedDescriptors;
        }

        IReadOnlyList<PersistedSettingDescriptor> descriptors = createDescriptors();
        _descriptorCache[(ownerType, ownerPrefix)] = descriptors;
        OperationSwitchTelemetry.SetTag(activity, "cache.hit", false);
        OperationSwitchTelemetry.SetTag(activity, "descriptor.count", descriptors.Count);
        return descriptors;
    }

    /// <summary>
    /// Reflects the persistable properties for one owner type.
    /// </summary>
    private static IReadOnlyList<PersistedSettingDescriptor> BuildDescriptors(Type ownerType, string ownerPrefix)
    {
        return ownerType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead)
            .Where(property => property.GetCustomAttribute<BrowsableAttribute>()?.Browsable != false)
            .Select(property =>
            {
                PersistedValueAttribute? attribute = property.GetCustomAttribute<PersistedValueAttribute>();
                string key = !string.IsNullOrWhiteSpace(attribute?.Key)
                    ? attribute!.Key!
                    : ownerPrefix + "." + ToCamelCase(property.Name);
                PersistenceScope writeScope = attribute?.WriteScope ?? PersistenceScope.UserTargetOverride;
                return new PersistedSettingDescriptor(property, key, writeScope);
            })
            .ToList();
    }

    /// <summary>
    /// Produces the stable generated-key owner prefix for an option set.
    /// </summary>
    private string GetOptionSetOwnerPrefix(Type optionSetType, TargetTypeId targetTypeId)
    {
        if (_optionSetPrefixCache.TryGetValue((optionSetType, targetTypeId.Value), out string? cachedPrefix))
        {
            return cachedPrefix;
        }

        string extensionId = ResolveExtensionIdForOptionSet(optionSetType);
        string ownerSegment = ResolveOwnerSegment(optionSetType);
        string prefix = string.IsNullOrWhiteSpace(extensionId)
            ? ownerSegment
            : extensionId + "." + ownerSegment;
        _optionSetPrefixCache[(optionSetType, targetTypeId.Value)] = prefix;
        return prefix;
    }

    /// <summary>
    /// Produces the stable generated-key owner prefix for a target-settings owner.
    /// </summary>
    private string GetTargetSettingsOwnerPrefix(Type settingsType, TargetTypeId targetTypeId)
    {
        if (_targetSettingsPrefixCache.TryGetValue((settingsType, targetTypeId.Value), out string? cachedPrefix))
        {
            return cachedPrefix;
        }

        string ownerSegment = ResolveOwnerSegment(settingsType);
        string prefix = string.IsNullOrWhiteSpace(ownerSegment) ? targetTypeId.Value : targetTypeId.Value + "." + ownerSegment;
        _targetSettingsPrefixCache[(settingsType, targetTypeId.Value)] = prefix;
        return prefix;
    }

    /// <summary>
    /// Resolves the extension id that owns one option-set type.
    /// </summary>
    private string ResolveExtensionIdForOptionSet(Type optionSetType)
    {
        string? ownedModuleId = _catalog.GetOwningModuleId(optionSetType.Assembly);
        if (!string.IsNullOrWhiteSpace(ownedModuleId))
        {
            return ownedModuleId;
        }

        return string.Empty;
    }

    /// <summary>
    /// Resolves the owner segment used when generating persistence keys.
    /// </summary>
    private string ResolveOwnerSegment(Type ownerType)
    {
        if (_typeOwnerPrefixCache.TryGetValue(ownerType, out string? cachedValue))
        {
            return cachedValue;
        }

        PersistedSettingsAttribute? attribute = ownerType.GetCustomAttribute<PersistedSettingsAttribute>();
        string segment = !string.IsNullOrWhiteSpace(attribute?.KeyPrefix)
            ? attribute!.KeyPrefix
            : ToCamelCase(TrimOwnerSuffix(ownerType.Name));
        _typeOwnerPrefixCache[ownerType] = segment;
        return segment;
    }

    /// <summary>
    /// Removes common owner suffixes so generated keys stay shorter and more intentional.
    /// </summary>
    private static string TrimOwnerSuffix(string ownerTypeName)
    {
        string[] suffixes = { "Options", "Settings", "Config" };
        foreach (string suffix in suffixes)
        {
            if (ownerTypeName.EndsWith(suffix, StringComparison.Ordinal) && ownerTypeName.Length > suffix.Length)
            {
                return ownerTypeName.Substring(0, ownerTypeName.Length - suffix.Length);
            }
        }

        return ownerTypeName;
    }

    /// <summary>
    /// Reads and serializes one reflected property value.
    /// </summary>
    private bool TryReadValueToken(object owner, PersistedSettingDescriptor descriptor, out JToken? token)
    {
        object? value = descriptor.Property.GetValue(owner);
        if (descriptor.IsOptionWrapper)
        {
            PropertyInfo? valueProperty = descriptor.Property.PropertyType.GetProperty("Value");
            value = value == null ? null : valueProperty?.GetValue(value);
        }

        return TrySerializeValue(descriptor.ValueType, value, out token);
    }

    /// <summary>
    /// Converts one runtime value into a persisted token using registered converters when available.
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
    /// Restores one runtime value from a persisted token using registered converters when available.
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
    /// Serializes primitive and collection values without requiring custom converters.
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
    /// Restores primitive and collection values without requiring custom converters.
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
            value = token.ToObject(nonNullableType, JsonSerializer.CreateDefault());
            return true;
        }

        if (typeof(IList).IsAssignableFrom(nonNullableType))
        {
            value = token.ToObject(nonNullableType, JsonSerializer.CreateDefault());
            return value != null;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Applies restored items into an existing collection-backed property.
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

    /// <summary>
    /// Converts a PascalCase identifier into the camelCase key segment used by generated setting ids.
    /// </summary>
    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (value.Length == 1)
        {
            return value.ToLowerInvariant();
        }

        return char.ToLowerInvariant(value[0]) + value.Substring(1);
    }

    /// <summary>
    /// Produces a filesystem-safe file name from the target key used by per-target appdata overrides.
    /// </summary>
    private static string SanitizeFileName(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        char[] characters = value.Select(character => invalidChars.Contains(character) ? '_' : character).ToArray();
        return new string(characters);
    }

    /// <summary>
    /// Validates generated keys once during construction so collisions fail fast at startup.
    /// </summary>
    private void ValidateGeneratedKeys()
    {
        Dictionary<string, string> seenKeys = new(StringComparer.Ordinal);

        foreach (Assembly assembly in GetKnownSettingsAssemblies())
        {
            foreach (Type optionSetType in assembly.GetTypes().Where(type => typeof(OperationOptions).IsAssignableFrom(type) && !type.IsAbstract))
            {
                ValidateDescriptorsForOwner(optionSetType, GetOptionSetOwnerPrefix(optionSetType, new TargetTypeId("target")), seenKeys);
            }
        }

        ValidateDescriptorsForOwner(typeof(TargetSettings), GetTargetSettingsOwnerPrefix(typeof(TargetSettings), new TargetTypeId("target")), seenKeys);
    }

    /// <summary>
    /// Validates one reflected owner type for duplicate persisted keys.
    /// </summary>
    private static void ValidateDescriptorsForOwner(Type ownerType, string ownerPrefix, IDictionary<string, string> seenKeys)
    {
        foreach (PersistedSettingDescriptor descriptor in BuildDescriptors(ownerType, ownerPrefix))
        {
            if (seenKeys.TryGetValue(descriptor.Key, out string? existingOwner) && !string.Equals(existingOwner, ownerType.FullName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Persisted setting key '{descriptor.Key}' is defined by both '{existingOwner}' and '{ownerType.FullName}'.");
            }

            seenKeys[descriptor.Key] = ownerType.FullName ?? ownerType.Name;
        }
    }

    /// <summary>
    /// Discovers the option-set types referenced by one runtime operation by walking its generic base hierarchy.
    /// </summary>
    private IEnumerable<Assembly> GetKnownSettingsAssemblies()
    {
        HashSet<Assembly> assemblies = new();
        foreach (OperationDescriptor descriptor in _catalog.OperationDescriptors)
        {
            assemblies.Add(descriptor.OperationType.Assembly);
        }

        foreach (TargetDescriptor descriptor in _catalog.TargetDescriptors)
        {
            assemblies.Add(descriptor.TargetType.Assembly);
        }

        assemblies.Add(typeof(TargetSettings).Assembly);
        return assemblies;
    }
}
