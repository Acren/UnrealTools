using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Describes a single editable field that can be rendered by a generic host-side option editor.
/// </summary>
public sealed class OptionFieldDescriptor
{
    /// <summary>
    /// Creates an option field descriptor with a stable identifier, display name, and field kind.
    /// </summary>
    public OptionFieldDescriptor(string id, string displayName, OptionFieldKind fieldKind, IEnumerable<OptionChoiceDescriptor>? choices = null)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        FieldKind = fieldKind;
        Choices = choices?.ToArray() ?? Array.Empty<OptionChoiceDescriptor>();
    }

    /// <summary>
    /// Gets the stable identifier used to persist or look up this field.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the user-facing label shown in generic editors.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the kind of editor the host should render for this field.
    /// </summary>
    public OptionFieldKind FieldKind { get; }

    /// <summary>
    /// Gets the selectable choices for fields that use discrete values.
    /// </summary>
    public IReadOnlyList<OptionChoiceDescriptor> Choices { get; }
}
