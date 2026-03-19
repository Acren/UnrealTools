using System;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Bundles the extension catalog and the application-facing discovery services so each UI host can bootstrap the
/// same LocalAutomation composition root with compile-time modules.
/// </summary>
public sealed class LocalAutomationApplicationHost
{
    /// <summary>
    /// Creates an application host around a populated extension catalog.
    /// </summary>
    public LocalAutomationApplicationHost(ExtensionCatalog catalog)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Execution = new ExecutionService();
        ExecutionRuntime = new ExecutionRuntimeService(catalog);
        Operations = new OperationCatalogService(catalog);
        OperationRuntime = new OperationRuntimeService(catalog);
        OperationSession = new OperationSessionService(Operations, OperationRuntime);
        Targets = new TargetDiscoveryService(catalog);
    }

    /// <summary>
    /// Gets the catalog containing the registered extension modules and their descriptors.
    /// </summary>
    public ExtensionCatalog Catalog { get; }

    /// <summary>
    /// Gets the service used to query registered operations for a target.
    /// </summary>
    public OperationCatalogService Operations { get; }

    /// <summary>
    /// Gets the service that tracks the shared execution session for UI hosts.
    /// </summary>
    public ExecutionService Execution { get; }

    /// <summary>
    /// Gets the service used to start shared execution sessions through extension-provided runner adapters.
    /// </summary>
    public ExecutionRuntimeService ExecutionRuntime { get; }

    /// <summary>
    /// Gets the service used to create and inspect runtime operations through extension-provided adapters.
    /// </summary>
    public OperationRuntimeService OperationRuntime { get; }

    /// <summary>
    /// Gets the service used to derive selected-operation UI state from the shared catalog and runtime adapters.
    /// </summary>
    public OperationSessionService OperationSession { get; }

    /// <summary>
    /// Gets the service used to create targets from host-provided paths or source values.
    /// </summary>
    public TargetDiscoveryService Targets { get; }

    /// <summary>
    /// Creates a host, registers the provided compile-time modules, and exposes the resulting services.
    /// </summary>
    public static LocalAutomationApplicationHost Create(params IExtensionModule[] modules)
    {
        ExtensionCatalog catalog = new();
        foreach (IExtensionModule module in modules)
        {
            catalog.RegisterModule(module);
        }

        return new LocalAutomationApplicationHost(catalog);
    }
}
