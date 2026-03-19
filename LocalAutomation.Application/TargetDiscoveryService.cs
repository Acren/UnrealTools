using System;
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
}
