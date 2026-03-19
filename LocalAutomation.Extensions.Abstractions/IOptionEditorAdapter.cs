using System;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Projects an extension-owned option object into a property-grid-friendly editor target so UI shells can render
/// complex option shapes without embedding extension-specific logic.
/// </summary>
public interface IOptionEditorAdapter
{
    /// <summary>
    /// Gets the stable adapter identifier for diagnostics and duplicate-registration checks.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Returns whether this adapter can project the provided runtime option-set instance.
    /// </summary>
    bool CanAdapt(object optionSet);

    /// <summary>
    /// Creates the editor target object bound into the property grid.
    /// </summary>
    object CreateEditorTarget(object optionSet);

    /// <summary>
    /// Refreshes an existing editor target from the latest runtime option-set state.
    /// </summary>
    void RefreshEditorTarget(object optionSet, object editorTarget);
}
