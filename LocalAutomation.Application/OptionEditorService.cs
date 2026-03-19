using System;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Resolves extension-provided property-grid adapter targets for runtime option sets while leaving simple option sets
/// bound directly to their raw model objects.
/// </summary>
public sealed class OptionEditorService
{
    private readonly ExtensionCatalog _catalog;

    /// <summary>
    /// Creates an option editor service around the shared extension catalog.
    /// </summary>
    public OptionEditorService(ExtensionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Returns the property-grid target object for the provided runtime option set.
    /// </summary>
    public object GetEditorTarget(object optionSet)
    {
        if (optionSet == null)
        {
            throw new ArgumentNullException(nameof(optionSet));
        }

        foreach (IOptionEditorAdapter adapter in _catalog.OptionEditorAdapters)
        {
            if (adapter.CanAdapt(optionSet))
            {
                return adapter.CreateEditorTarget(optionSet);
            }
        }

        return optionSet;
    }
}
