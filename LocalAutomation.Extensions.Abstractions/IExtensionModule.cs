using System.Collections.Generic;
using System.Reflection;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Represents a compile-time extension module that contributes targets, operations, and other descriptors to the
/// host application.
/// </summary>
public interface IExtensionModule
{
    /// <summary>
    /// Gets the stable identifier for the extension module.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the display name shown by tooling or diagnostics when this extension is registered.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Registers this extension's contributions into the provided registry.
    /// </summary>
    void Register(IExtensionRegistry registry);

    /// <summary>
    /// Returns the assemblies that contain this module's discoverable attributed targets and operations. Modules that
    /// keep their descriptors beside the module type can rely on the default implementation.
    /// </summary>
    IEnumerable<Assembly> GetDescriptorAssemblies()
    {
        return new[] { GetType().Assembly };
    }
}
