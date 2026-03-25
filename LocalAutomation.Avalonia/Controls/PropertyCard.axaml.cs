using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Wraps a standard property grid in the same card shell used across settings and operation option panels.
/// </summary>
public partial class PropertyCard : UserControl
{
    /// <summary>
    /// Identifies the card title property.
    /// </summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PropertyCard, string?>(nameof(Title));

    /// <summary>
    /// Identifies the property-grid target object rendered inside the card.
    /// </summary>
    public static readonly StyledProperty<object?> PropertyGridTargetProperty =
        AvaloniaProperty.Register<PropertyCard, object?>(nameof(PropertyGridTarget));

    /// <summary>
    /// Creates the reusable property card control.
    /// </summary>
    public PropertyCard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the card title shown above the property grid.
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Gets or sets the object currently presented to the property grid.
    /// </summary>
    public object? PropertyGridTarget
    {
        get => GetValue(PropertyGridTargetProperty);
        set => SetValue(PropertyGridTargetProperty, value);
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the property card.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
