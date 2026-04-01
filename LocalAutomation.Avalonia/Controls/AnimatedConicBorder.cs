using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Draws an animated border by rotating a conic brush with two opposing highlight wedges.
/// </summary>
public sealed class AnimatedConicBorder : Border
{
    private const double DefaultAngleStep = 6;
    private static readonly TimeSpan DefaultAnimationInterval = TimeSpan.FromMilliseconds(33);

    private readonly DispatcherTimer _animationTimer;
    private double _angle;

    /// <summary>
    /// Identifies whether the border animation should be visible and advancing.
    /// </summary>
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<AnimatedConicBorder, bool>(nameof(IsAnimated));

    /// <summary>
    /// Identifies the number of degrees advanced on each animation tick.
    /// </summary>
    public static readonly StyledProperty<double> AngleStepProperty =
        AvaloniaProperty.Register<AnimatedConicBorder, double>(nameof(AngleStep), DefaultAngleStep);

    /// <summary>
    /// Identifies the interval between border animation ticks.
    /// </summary>
    public static readonly StyledProperty<TimeSpan> AnimationIntervalProperty =
        AvaloniaProperty.Register<AnimatedConicBorder, TimeSpan>(nameof(AnimationInterval), DefaultAnimationInterval);

    /// <summary>
    /// Identifies the conic brush template used to generate the animated border frames.
    /// </summary>
    public static readonly StyledProperty<ConicGradientBrush?> AnimationBrushProperty =
        AvaloniaProperty.Register<AnimatedConicBorder, ConicGradientBrush?>(nameof(AnimationBrush));

    /// <summary>
    /// Creates the animated border control and prepares its local timer.
    /// </summary>
    public AnimatedConicBorder()
    {
        /* Keep the animation local to the control so callers can opt in anywhere they need an animated conic border
           without introducing any shared animation infrastructure or custom drawing surface. */
        _animationTimer = new DispatcherTimer { Interval = AnimationInterval };
        _animationTimer.Tick += HandleAnimationTick;

        BorderBrush = Brushes.Transparent;
        DetachedFromVisualTree += HandleDetachedFromVisualTree;
    }

    /// <summary>
    /// Gets or sets whether the border animation is active.
    /// </summary>
    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    /// <summary>
    /// Gets or sets how many degrees the border rotates per timer tick.
    /// </summary>
    public double AngleStep
    {
        get => GetValue(AngleStepProperty);
        set => SetValue(AngleStepProperty, value);
    }

    /// <summary>
    /// Gets or sets the interval between animation ticks.
    /// </summary>
    public TimeSpan AnimationInterval
    {
        get => GetValue(AnimationIntervalProperty);
        set => SetValue(AnimationIntervalProperty, value);
    }

    /// <summary>
    /// Gets or sets the conic brush template used to paint the animated border.
    /// </summary>
    public ConicGradientBrush? AnimationBrush
    {
        get => GetValue(AnimationBrushProperty);
        set => SetValue(AnimationBrushProperty, value);
    }

    /// <summary>
    /// Restarts the timer if the control comes back into view while still animated.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAnimationState();
    }

    /// <summary>
    /// Reacts to configuration changes by refreshing the timer or brush immediately.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsAnimatedProperty)
        {
            UpdateAnimationState();
            return;
        }

        if (change.Property == AnimationIntervalProperty)
        {
            _animationTimer.Interval = AnimationInterval;
            return;
        }

        if (change.Property == AngleStepProperty ||
            change.Property == AnimationBrushProperty)
        {
            if (IsAnimated)
            {
                BorderBrush = CreateAnimatedBrushFrame(_angle);
            }
        }
    }

    /// <summary>
    /// Stops the timer when the border leaves the visual tree so inactive visuals do not keep ticking.
    /// </summary>
    private void HandleDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _animationTimer.Stop();
    }

    /// <summary>
    /// Advances the conic angle in small steady steps so the opposing highlights orbit smoothly.
    /// </summary>
    private void HandleAnimationTick(object? sender, EventArgs e)
    {
        _angle = (_angle + AngleStep) % 360;
        BorderBrush = CreateAnimatedBrushFrame(_angle);
    }

    /// <summary>
    /// Starts or stops the animation based on activation and attachment state, restoring a neutral border when inactive.
    /// </summary>
    private void UpdateAnimationState()
    {
        if (!IsAnimated || VisualRoot == null)
        {
            _animationTimer.Stop();
            BorderBrush = Brushes.Transparent;
            return;
        }

        _animationTimer.Interval = AnimationInterval;
        BorderBrush = CreateAnimatedBrushFrame(_angle);
        if (!_animationTimer.IsEnabled)
        {
            _animationTimer.Start();
        }
    }

    /// <summary>
    /// Clones the configured conic brush template and rotates it by the current animation angle.
     /// </summary>
    private IBrush CreateAnimatedBrushFrame(double angle)
    {
        ConicGradientBrush template = AnimationBrush ?? CreateDefaultAnimationBrush();
        GradientStops gradientStops = new();
        foreach (GradientStop stop in template.GradientStops)
        {
            gradientStops.Add(new GradientStop(stop.Color, stop.Offset));
        }

        return new ConicGradientBrush
        {
            Center = template.Center,
            Angle = template.Angle + angle,
            Opacity = template.Opacity,
            SpreadMethod = template.SpreadMethod,
            Transform = template.Transform,
            TransformOrigin = template.TransformOrigin,
            GradientStops = gradientStops
        };
    }

    /// <summary>
    /// Provides a fallback two-highlight conic brush so the control still works when no style supplies one.
    /// </summary>
    private static ConicGradientBrush CreateDefaultAnimationBrush()
    {
        return new ConicGradientBrush
        {
            Center = RelativePoint.Center,
            GradientStops = new GradientStops
            {
                /* Keep the control neutral by default so callers opt into accent color through styling rather than
                   inheriting a task-card-specific blue treatment. */
                new GradientStop(Color.Parse("#6E757EFF"), 0.00),
                new GradientStop(Color.Parse("#6E757EFF"), 0.20),
                new GradientStop(Color.Parse("#AAB3BDFF"), 0.24),
                new GradientStop(Color.Parse("#F2F5F8FF"), 0.27),
                new GradientStop(Color.Parse("#AAB3BDFF"), 0.30),
                new GradientStop(Color.Parse("#6E757EFF"), 0.34),
                new GradientStop(Color.Parse("#6E757EFF"), 0.70),
                new GradientStop(Color.Parse("#AAB3BDFF"), 0.74),
                new GradientStop(Color.Parse("#F2F5F8FF"), 0.77),
                new GradientStop(Color.Parse("#AAB3BDFF"), 0.80),
                new GradientStop(Color.Parse("#6E757EFF"), 0.84),
                new GradientStop(Color.Parse("#6E757EFF"), 1.00),
            }
        };
    }
}
