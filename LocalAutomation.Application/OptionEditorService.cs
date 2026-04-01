using System;
using System.Collections.Generic;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Resolves extension-provided property-grid adapter targets for runtime option sets while leaving simple option sets
/// bound directly to their raw model objects.
/// </summary>
public sealed class OptionEditorService
{
    private readonly ExtensionCatalog _catalog;
    private readonly Dictionary<object, EditorBinding> _bindings = new(ReferenceEqualityComparer.Instance);

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

        using PerformanceActivityScope activity = PerformanceTelemetry.StartActivity("GetEditorTarget")
            .SetTag("option_set.type", optionSet.GetType().Name);

        if (_bindings.TryGetValue(optionSet, out EditorBinding? binding))
        {
            activity.SetTag("cache.hit", true)
                .SetTag("adapter.type", binding.Adapter.GetType().Name)
                .SetTag("editor_target.type", binding.EditorTarget.GetType().Name);
            binding.Adapter.RefreshEditorTarget(optionSet, binding.EditorTarget);
            return binding.EditorTarget;
        }

        foreach (IOptionEditorAdapter adapter in _catalog.OptionEditorAdapters)
        {
            if (adapter.CanAdapt(optionSet))
            {
                object editorTarget = adapter.CreateEditorTarget(optionSet);
                _bindings[optionSet] = new EditorBinding(adapter, editorTarget);
                activity.SetTag("cache.hit", false)
                    .SetTag("adapter.type", adapter.GetType().Name)
                    .SetTag("editor_target.type", editorTarget.GetType().Name);
                return editorTarget;
            }
        }

        activity.SetTag("cache.hit", false)
            .SetTag("editor_target.type", optionSet.GetType().Name);
        return optionSet;
    }

    /// <summary>
    /// Stores the adapter/editor-target pair for one live option-set instance so adapter-backed editors can be reused
    /// and refreshed instead of recreated with every UI rebuild.
    /// </summary>
    private sealed class EditorBinding
    {
        /// <summary>
        /// Creates one stored editor binding for a live option-set instance.
        /// </summary>
        public EditorBinding(IOptionEditorAdapter adapter, object editorTarget)
        {
            Adapter = adapter;
            EditorTarget = editorTarget;
        }

        /// <summary>
        /// Gets the adapter that created the editor target.
        /// </summary>
        public IOptionEditorAdapter Adapter { get; }

        /// <summary>
        /// Gets the cached editor target bound into the property grid.
        /// </summary>
        public object EditorTarget { get; }
    }

    /// <summary>
    /// Uses reference equality for runtime option-set objects so editor bindings remain tied to the exact live
    /// instances held by the parameter session.
    /// </summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        /// <summary>
        /// Gets the shared comparer instance.
        /// </summary>
        public static ReferenceEqualityComparer Instance { get; } = new();

        /// <summary>
        /// Returns whether the two objects are the same runtime instance.
        /// </summary>
        public new bool Equals(object? x, object? y)
        {
            return ReferenceEquals(x, y);
        }

        /// <summary>
        /// Returns an identity-based hash code for the provided runtime object.
        /// </summary>
        public int GetHashCode(object obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
