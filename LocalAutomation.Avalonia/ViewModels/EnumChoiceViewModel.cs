namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Represents a single enum-backed choice in the generic Avalonia option editor.
/// </summary>
public sealed class EnumChoiceViewModel
{
    /// <summary>
    /// Creates an enum choice with the raw value and its display label.
    /// </summary>
    public EnumChoiceViewModel(object value, string label)
    {
        Value = value;
        Label = label;
    }

    /// <summary>
    /// Gets the raw enum value.
    /// </summary>
    public object Value { get; }

    /// <summary>
    /// Gets the display label rendered in the UI.
    /// </summary>
    public string Label { get; }
}
