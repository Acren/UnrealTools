using System;
using System.Reflection;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Creates registered extension targets from host-provided source values so path parsing and target detection no
/// longer need to live directly inside UI code.
/// </summary>
public sealed class TargetDiscoveryService
{
    private readonly ExtensionCatalog _catalog;

    /// <summary>
    /// Creates a target discovery service around the shared extension catalog.
    /// </summary>
    public TargetDiscoveryService(ExtensionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Tries each registered target factory until one can create a target from the provided source value.
    /// </summary>
    public bool TryCreateTarget(string source, out object? target)
    {
        foreach (ITargetFactory factory in _catalog.TargetFactories)
        {
            if (factory.TryCreateTarget(source, out target))
            {
                return true;
            }
        }

        target = null;
        return false;
    }

    /// <summary>
    /// Creates a target from the provided source value or throws when no registered factory recognizes it.
    /// </summary>
    public object CreateTarget(string source)
    {
        if (TryCreateTarget(source, out object? target) && target != null)
        {
            return target;
        }

        throw new InvalidOperationException($"No registered target factory accepted '{source}'.");
    }

    /// <summary>
    /// Returns the stable target descriptor identifier for the provided runtime target when one exists.
    /// </summary>
    public string? GetTargetTypeId(object? target)
    {
        if (target == null)
        {
            return null;
        }

        foreach (TargetDescriptor descriptor in _catalog.TargetDescriptors)
        {
            if (descriptor.TargetType.IsInstanceOfType(target))
            {
                return descriptor.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns whether the provided object matches any registered target descriptor.
    /// </summary>
    public bool IsTarget(object? target)
    {
        return GetTargetTypeId(target) != null;
    }

    /// <summary>
    /// Returns whether the provided target currently reports itself as valid.
    /// </summary>
    public bool IsValidTarget(object target)
    {
        return GetRequiredPropertyValue<bool>(target, "IsValid");
    }

    /// <summary>
    /// Returns the user-facing display name for the provided target.
    /// </summary>
    public string GetDisplayName(object target)
    {
        return GetRequiredPropertyValue<string>(target, "DisplayName");
    }

    /// <summary>
    /// Returns the user-facing type name for the provided target.
    /// </summary>
    public string GetTypeName(object target)
    {
        return GetRequiredPropertyValue<string>(target, "TypeName");
    }

    /// <summary>
    /// Returns the stable path or source string for the provided target.
    /// </summary>
    public string GetTargetPath(object target)
    {
        return GetRequiredPropertyValue<string>(target, "TargetPath");
    }

    /// <summary>
    /// Returns the runtime type name for the provided target so persistence can disambiguate target kinds that share a
    /// path.
    /// </summary>
    public string GetRuntimeTypeName(object target)
    {
        return target?.GetType().FullName ?? throw new ArgumentNullException(nameof(target));
    }

    /// <summary>
    /// Reads a required public property value from the current bridge-era runtime target object.
    /// </summary>
    private static T GetRequiredPropertyValue<T>(object target, string propertyName)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        PropertyInfo? property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property == null)
        {
            throw new InvalidOperationException($"Target type '{target.GetType().FullName}' does not expose required property '{propertyName}'.");
        }

        object? value = property.GetValue(target);
        if (value is T typedValue)
        {
            return typedValue;
        }

        throw new InvalidOperationException($"Target property '{propertyName}' on '{target.GetType().FullName}' is not a '{typeof(T).FullName}'.");
    }
}
