using System;
using System.Collections.Generic;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Resolves extension-provided target context actions for the currently selected runtime target.
/// </summary>
public sealed class ContextActionService
{
    private readonly ExtensionCatalog _catalog;

    /// <summary>
    /// Creates a context action service around the shared extension catalog.
    /// </summary>
    public ContextActionService(ExtensionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Returns the actions currently applicable to the provided runtime target.
    /// </summary>
    public IReadOnlyList<ContextActionDescriptor> GetActionsForTarget(object? target)
    {
        if (target == null)
        {
            return Array.Empty<ContextActionDescriptor>();
        }

        List<ContextActionDescriptor> actions = new();
        foreach (ContextActionDescriptor descriptor in _catalog.ContextActions)
        {
            if (descriptor.AppliesTo(target))
            {
                actions.Add(descriptor);
            }
        }

        return actions;
    }
}
