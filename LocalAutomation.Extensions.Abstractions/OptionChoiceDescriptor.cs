namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Represents a single selectable option value for metadata-driven editors.
/// </summary>
public sealed class OptionChoiceDescriptor
{
    /// <summary>
    /// Creates a selectable option choice with a user-facing label and a persisted value.
    /// </summary>
    public OptionChoiceDescriptor(string label, string value)
    {
        Label = label;
        Value = value;
    }

    /// <summary>
    /// Gets the display label shown in the host UI.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the persisted value associated with the displayed label.
    /// </summary>
    public string Value { get; }
}
