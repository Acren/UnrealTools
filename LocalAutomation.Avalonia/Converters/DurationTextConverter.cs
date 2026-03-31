using System;
using System.Globalization;
using Avalonia.Data.Converters;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Converters;

/// <summary>
/// Formats raw nullable durations for display in the execution metrics strip so time-text composition stays in the view layer.
/// </summary>
public sealed class DurationTextConverter : IValueConverter
{
    /// <summary>
    /// Converts a raw nullable duration into the compact display format used by the execution graph and header metrics.
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is TimeSpan duration
            ? ExecutionGraphViewModel.FormatDuration(duration)
            : ExecutionGraphViewModel.FormatDuration(null);
    }

    /// <summary>
    /// Leaves the source duration unchanged because metrics-strip time formatting is view-only.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value;
    }
}
