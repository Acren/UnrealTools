using System;
using System.Collections.Generic;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Stores registered extension modules and their contributed descriptors for use by application services and UI
/// hosts.
/// </summary>
public sealed class ExtensionCatalog : IExtensionRegistry
{
    private readonly List<ContextActionDescriptor> _contextActions = new();
    private readonly List<IExtensionModule> _modules = new();
    private readonly List<IOptionEditorAdapter> _optionEditorAdapters = new();
    private readonly List<IOptionValueConverter> _optionValueConverters = new();
    private readonly List<OperationDescriptor> _operations = new();
    private readonly List<TargetDescriptor> _targets = new();
    private readonly List<ITargetFactory> _targetFactories = new();

    /// <summary>
    /// Gets the registered extension modules in registration order.
    /// </summary>
    public IReadOnlyList<IExtensionModule> Modules => _modules;

    /// <summary>
    /// Gets the registered target descriptors.
    /// </summary>
    public IReadOnlyList<TargetDescriptor> TargetDescriptors => _targets;

    /// <summary>
    /// Gets the registered operation descriptors.
    /// </summary>
    public IReadOnlyList<OperationDescriptor> OperationDescriptors => _operations;

    /// <summary>
    /// Gets the registered target factories.
    /// </summary>
    public IReadOnlyList<ITargetFactory> TargetFactories => _targetFactories;

    /// <summary>
    /// Gets the registered target context actions.
    /// </summary>
    public IReadOnlyList<ContextActionDescriptor> ContextActions => _contextActions;

    /// <summary>
    /// Gets the registered option editor adapters.
    /// </summary>
    public IReadOnlyList<IOptionEditorAdapter> OptionEditorAdapters => _optionEditorAdapters;

    /// <summary>
    /// Gets the registered option value converters.
    /// </summary>
    public IReadOnlyList<IOptionValueConverter> OptionValueConverters => _optionValueConverters;

    /// <summary>
    /// Registers a module once and lets it contribute its descriptors through the shared registry interface.
    /// </summary>
    public void RegisterModule(IExtensionModule module)
    {
        if (module == null)
        {
            throw new ArgumentNullException(nameof(module));
        }

        EnsureUniqueModule(module);
        module.Register(this);
        _modules.Add(module);
    }

    /// <summary>
    /// Registers a target descriptor after checking that its identifier is unique.
    /// </summary>
    public void RegisterTarget(TargetDescriptor descriptor)
    {
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        EnsureUniqueId(descriptor.Id, _targets, static item => item.Id, nameof(descriptor));
        _targets.Add(descriptor);
    }

    /// <summary>
    /// Registers an operation descriptor after checking that its identifier is unique.
    /// </summary>
    public void RegisterOperation(OperationDescriptor descriptor)
    {
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        EnsureUniqueId(descriptor.Id, _operations, static item => item.Id, nameof(descriptor));
        _operations.Add(descriptor);
    }

    /// <summary>
    /// Registers a target factory after checking that its identifier is unique.
    /// </summary>
    public void RegisterTargetFactory(ITargetFactory factory)
    {
        if (factory == null)
        {
            throw new ArgumentNullException(nameof(factory));
        }

        EnsureUniqueId(factory.Id, _targetFactories, static item => item.Id, nameof(factory));
        _targetFactories.Add(factory);
    }

    /// Registers a context action after checking that its identifier is unique.
    /// </summary>
    public void RegisterContextAction(ContextActionDescriptor descriptor)
    {
        if (descriptor == null)
        {
            throw new ArgumentNullException(nameof(descriptor));
        }

        EnsureUniqueId(descriptor.Id, _contextActions, static item => item.Id, nameof(descriptor));
        _contextActions.Add(descriptor);
    }

    /// <summary>
    /// Registers an option editor adapter after checking that its identifier is unique.
    /// </summary>
    public void RegisterOptionEditorAdapter(IOptionEditorAdapter adapter)
    {
        if (adapter == null)
        {
            throw new ArgumentNullException(nameof(adapter));
        }

        EnsureUniqueId(adapter.Id, _optionEditorAdapters, static item => item.Id, nameof(adapter));
        _optionEditorAdapters.Add(adapter);
    }

    /// <summary>
    /// Registers an option value converter after checking that its identifier is unique.
    /// </summary>
    public void RegisterOptionValueConverter(IOptionValueConverter converter)
    {
        if (converter == null)
        {
            throw new ArgumentNullException(nameof(converter));
        }

        EnsureUniqueId(converter.Id, _optionValueConverters, static item => item.Id, nameof(converter));
        _optionValueConverters.Add(converter);
    }

    /// <summary>
    /// Prevents the same module from being registered more than once because duplicate registration would duplicate
    /// its contributed descriptors.
    /// </summary>
    private void EnsureUniqueModule(IExtensionModule module)
    {
        foreach (IExtensionModule existingModule in _modules)
        {
            if (string.Equals(existingModule.Id, module.Id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Extension module '{module.Id}' is already registered.");
            }
        }
    }

    /// <summary>
    /// Prevents duplicate identifiers inside a single descriptor category so future lookup remains deterministic.
    /// </summary>
    private static void EnsureUniqueId<T>(string id, IEnumerable<T> items, Func<T, string> selector, string paramName)
    {
        foreach (T item in items)
        {
            if (string.Equals(selector(item), id, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"A descriptor with id '{id}' is already registered.");
            }
        }
    }
}
