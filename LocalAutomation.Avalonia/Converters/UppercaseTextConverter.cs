using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace LocalAutomation.Avalonia.Converters;

/// <summary>
/// Normalizes display text to uppercase in the view layer so view models can keep their original casing.
/// </summary>
public sealed class UppercaseTextConverter : IValueConverter
{
    /// <summary>
    /// Converts display text to uppercase using the current culture.
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string text ? text.ToUpper(culture) : value;
    }

    /// <summary>
    /// Leaves source values unchanged because uppercase formatting is view-only.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
