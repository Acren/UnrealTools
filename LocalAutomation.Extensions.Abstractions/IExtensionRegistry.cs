using System.Collections.Generic;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Collects extension-provided descriptors and factories so the host can discover available capabilities without
/// knowing extension details ahead of time.
/// </summary>
public interface IExtensionRegistry
{
    /// <summary>
    /// Gets the registered target descriptors.
    /// </summary>
    IReadOnlyList<TargetDescriptor> TargetDescriptors { get; }

    /// <summary>
    /// Gets the registered operation descriptors.
    /// </summary>
    IReadOnlyList<OperationDescriptor> OperationDescriptors { get; }

    /// <summary>
    /// Gets the registered target factories used to create target instances from paths or other source data.
    /// </summary>
    IReadOnlyList<ITargetFactory> TargetFactories { get; }

    /// <summary>
    /// Gets the registered target adapters used to inspect extension-owned runtime targets.
    /// </summary>
    IReadOnlyList<ITargetAdapter> TargetAdapters { get; }

    /// <summary>
    /// Gets the registered target context actions.
    /// </summary>
    IReadOnlyList<ContextActionDescriptor> ContextActions { get; }

    /// <summary>
    /// Gets the registered option editor adapters.
    /// </summary>
    IReadOnlyList<IOptionEditorAdapter> OptionEditorAdapters { get; }

    /// <summary>
    /// Gets the registered option value converters.
    /// </summary>
    IReadOnlyList<IOptionValueConverter> OptionValueConverters { get; }

    /// <summary>
    /// Adds a target descriptor to the registry.
    /// </summary>
    void RegisterTarget(TargetDescriptor descriptor);

    /// <summary>
    /// Adds an operation descriptor to the registry.
    /// </summary>
    void RegisterOperation(OperationDescriptor descriptor);

    /// <summary>
    /// Adds a target factory to the registry.
    /// </summary>
    void RegisterTargetFactory(ITargetFactory factory);

    /// <summary>
    /// Adds a target adapter to the registry.
    /// </summary>
    void RegisterTargetAdapter(ITargetAdapter adapter);

    /// <summary>
    /// Adds a context action descriptor to the registry.
    /// </summary>
    void RegisterContextAction(ContextActionDescriptor descriptor);

    /// <summary>
    /// Adds an option editor adapter to the registry.
    /// </summary>
    void RegisterOptionEditorAdapter(IOptionEditorAdapter adapter);

    /// <summary>
    /// Adds an option value converter to the registry.
    /// </summary>
    void RegisterOptionValueConverter(IOptionValueConverter converter);
}
