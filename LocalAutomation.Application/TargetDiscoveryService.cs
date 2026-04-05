using System;
using LocalAutomation.Extensions.Abstractions;
using LocalAutomation.Runtime;

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
    public bool TryCreateTarget(string source, out IOperationTarget? target)
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
    public IOperationTarget CreateTarget(string source)
    {
        if (TryCreateTarget(source, out IOperationTarget? target) && target != null)
        {
            return target;
        }

        throw new InvalidOperationException($"No registered target factory accepted '{source}'.");
    }

    /// <summary>
    /// Returns the stable target descriptor identifier for the provided runtime target when one exists.
    /// </summary>
    public TargetTypeId? GetTargetTypeId(IOperationTarget? target)
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
    public bool IsTarget(IOperationTarget? target)
    {
        return GetTargetTypeId(target) != null;
    }

    /// <summary>
    /// Returns whether the provided target currently reports itself as valid.
    /// </summary>
    public bool IsValidTarget(IOperationTarget target)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        return target.IsValid;
    }

    /// <summary>
    /// Returns the user-facing display name for the provided target.
    /// </summary>
    public string GetDisplayName(IOperationTarget target)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        return target.DisplayName;
    }

    /// <summary>
    /// Returns the user-facing type name for the provided target.
    /// </summary>
    public string GetTypeName(IOperationTarget target)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        return target.TypeName;
    }

    /// <summary>
    /// Returns the stable path or source string for the provided target.
    /// </summary>
    public string GetTargetPath(IOperationTarget target)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        return target.TargetPath;
    }

    /// <summary>
    /// Returns the runtime type name for the provided target so persistence can disambiguate target kinds that share a
    /// path.
    /// </summary>
    public string GetRuntimeTypeName(IOperationTarget target)
    {
        return target?.GetType().FullName ?? throw new ArgumentNullException(nameof(target));
    }
}
