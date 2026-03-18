namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Enumerates the metadata-driven field kinds supported by the initial generic option editor contract.
/// </summary>
public enum OptionFieldKind
{
    /// <summary>
    /// Renders a boolean value such as a checkbox or toggle.
    /// </summary>
    Boolean,

    /// <summary>
    /// Renders a free-form string input.
    /// </summary>
    String,

    /// <summary>
    /// Renders a single path value.
    /// </summary>
    Path,

    /// <summary>
    /// Renders a fixed set of enum-style choices.
    /// </summary>
    Enum,

    /// <summary>
    /// Renders a single choice from a predefined list.
    /// </summary>
    SingleSelect,

    /// <summary>
    /// Renders multiple choices from a predefined list.
    /// </summary>
    MultiSelect
}
