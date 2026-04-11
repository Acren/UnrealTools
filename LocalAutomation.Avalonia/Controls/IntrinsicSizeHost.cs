using Avalonia;
using Avalonia.Controls;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Hosts visible node content and exposes the child's unconstrained intrinsic size alongside the normal arranged layout
/// pass so parent controls can react to real XAML width changes without creating detached duplicate controls.
/// </summary>
public class IntrinsicSizeHost : Decorator
{
    private Size _intrinsicSize;

    /// <summary>
    /// Identifies the most recent intrinsic width computed from the hosted content.
    /// </summary>
    public static readonly DirectProperty<IntrinsicSizeHost, double> IntrinsicWidthProperty =
        AvaloniaProperty.RegisterDirect<IntrinsicSizeHost, double>(
            nameof(IntrinsicWidth),
            host => host.IntrinsicWidth);

    /// <summary>
    /// Identifies the most recent intrinsic height computed from the hosted content.
    /// </summary>
    public static readonly DirectProperty<IntrinsicSizeHost, double> IntrinsicHeightProperty =
        AvaloniaProperty.RegisterDirect<IntrinsicSizeHost, double>(
            nameof(IntrinsicHeight),
            host => host.IntrinsicHeight);

    /// <summary>
    /// Gets the latest unconstrained intrinsic width reported by the hosted content.
    /// </summary>
    public double IntrinsicWidth => _intrinsicSize.Width;

    /// <summary>
    /// Gets the latest unconstrained intrinsic height reported by the hosted content.
    /// </summary>
    public double IntrinsicHeight => _intrinsicSize.Height;

    /// <summary>
    /// Measures the child twice: once unconstrained to capture its intrinsic desired size, then again with the real
    /// available size so the visible control tree still participates in Avalonia layout normally.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child == null)
        {
            return default;
        }

        Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size intrinsicSize = Child.DesiredSize;
        if (!intrinsicSize.Equals(_intrinsicSize))
        {
            Size previousIntrinsicSize = _intrinsicSize;
            _intrinsicSize = intrinsicSize;
            RaisePropertyChanged(IntrinsicWidthProperty, previousIntrinsicSize.Width, intrinsicSize.Width);
            RaisePropertyChanged(IntrinsicHeightProperty, previousIntrinsicSize.Height, intrinsicSize.Height);
        }

        Child.Measure(availableSize);
        return Child.DesiredSize;
    }

    /// <summary>
    /// Arranges the hosted content inside the slot chosen by the parent layout container.
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
