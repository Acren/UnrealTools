using Avalonia;
using Avalonia.Controls;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Hosts arbitrary content and exposes the last desired size computed by Avalonia's normal layout measurement so parent
/// controls can react to geometry changes without manually measuring named child elements.
/// </summary>
public class MeasuredSizeHost : Decorator
{
    private Size _measuredSize;

    /// <summary>
    /// Identifies the most recent desired width produced by the hosted content.
    /// </summary>
    public static readonly DirectProperty<MeasuredSizeHost, double> MeasuredWidthProperty =
        AvaloniaProperty.RegisterDirect<MeasuredSizeHost, double>(
            nameof(MeasuredWidth),
            host => host.MeasuredWidth);

    /// <summary>
    /// Identifies the most recent desired height produced by the hosted content.
    /// </summary>
    public static readonly DirectProperty<MeasuredSizeHost, double> MeasuredHeightProperty =
        AvaloniaProperty.RegisterDirect<MeasuredSizeHost, double>(
            nameof(MeasuredHeight),
            host => host.MeasuredHeight);

    /// <summary>
    /// Gets the latest desired width reported by the hosted content.
    /// </summary>
    public double MeasuredWidth => _measuredSize.Width;

    /// <summary>
    /// Gets the latest desired height reported by the hosted content.
    /// </summary>
    public double MeasuredHeight => _measuredSize.Height;

    /// <summary>
    /// Measures the hosted content and records the desired size so callers can react to the same geometry Avalonia uses.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        Child?.Measure(availableSize);
        Size desiredSize = Child?.DesiredSize ?? default;
        if (!desiredSize.Equals(_measuredSize))
        {
            Size previousMeasuredSize = _measuredSize;
            _measuredSize = desiredSize;
            RaisePropertyChanged(MeasuredWidthProperty, previousMeasuredSize.Width, desiredSize.Width);
            RaisePropertyChanged(MeasuredHeightProperty, previousMeasuredSize.Height, desiredSize.Height);
        }

        return desiredSize;
    }

    /// <summary>
    /// Arranges the hosted content to fill the slot already chosen by the parent layout container.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
