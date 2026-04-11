using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders one execution-graph task card so graph canvas code only needs to place it and connect graph interactions.
/// </summary>
public partial class ExecutionTaskCard : ExecutionGraphNodeControlBase
{
    private Border? _backgroundShell;
    private Border? _borderChrome;
    private Border? _clickSurface;
    private AnimatedConicBorder? _animatedBorderChrome;

    /// <summary>
    /// Identifies the rendered height for the task card.
    /// </summary>
    public static readonly StyledProperty<double> CardHeightProperty =
        AvaloniaProperty.Register<ExecutionTaskCard, double>(nameof(CardHeight));

    /// <summary>
    /// Creates the XAML-backed execution-graph task card control.
    /// </summary>
    public ExecutionTaskCard()
    {
        InitializeComponent();
        _clickSurface = GetRequiredControl<Border>("ClickSurface");
        _backgroundShell = GetRequiredControl<Border>("CardBackgroundShell");
        _borderChrome = GetRequiredControl<Border>("CardBorderChrome");
        _animatedBorderChrome = GetRequiredAnimatedBorder("AnimatedBorderChrome");
        InitializeNodeControl(clickSurfaceName: "ClickSurface", intrinsicHostName: "ContentIntrinsicHost");
    }

    /// <summary>
    /// Gets or sets the rendered height for the task card.
    /// </summary>
    public double CardHeight
    {
        get => GetValue(CardHeightProperty);
        set => SetValue(CardHeightProperty, value);
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the task card.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Looks up the dedicated animated border control that owns the reusable conic border animation.
    /// </summary>
    private AnimatedConicBorder GetRequiredAnimatedBorder(string name)
    {
        return GetRequiredControl<AnimatedConicBorder>(name);
    }

    /// <summary>
    /// Reapplies status-owned visuals whenever the nested task view model changes in a way that affects semantic styling
    /// or the running animation state.
    /// </summary>
    protected override void HandleObservedTaskPropertyChangedCore(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName) ||
            string.Equals(propertyName, nameof(ExecutionTaskViewModel.State), StringComparison.Ordinal) ||
            string.Equals(propertyName, nameof(ExecutionTaskViewModel.DisplayStatus), StringComparison.Ordinal))
        {
            ApplySemanticClasses();
        }
    }

    /// <summary>
    /// Applies the task-card semantic classes consumed by the local Avalonia styles and forwards whether the extracted
    /// animated border control should be active for the current task status.
    /// </summary>
    protected override void ApplySemanticClassesCore(ExecutionNodeViewModel observedNode, bool isHovered, bool isPressed)
    {
        /* The outer click surface carries whole-card opacity treatment, while the inner shells still own fill and stroke
           styling. All three layers need the same semantic classes so the styles can compose predictably. */
        ExecutionStatusClasses.ApplyInteractionClasses(_clickSurface!.Classes, observedNode.IsSelected, isHovered, isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(_clickSurface.Classes, observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyInteractionClasses(_backgroundShell!.Classes, observedNode.IsSelected, isHovered, isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(_backgroundShell.Classes, observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyInteractionClasses(_borderChrome!.Classes, observedNode.IsSelected, isHovered, isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(_borderChrome.Classes, observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyStatusClasses(_animatedBorderChrome!.Classes, observedNode.Task.DisplayStatus);

        /* Let the extracted control own the timer and conic brush creation while the task card only forwards whether the
           current task status should display the animated accent treatment. */
        _animatedBorderChrome.IsAnimated = observedNode.Task.State == global::LocalAutomation.Runtime.ExecutionTaskState.Running;
    }

    /// <summary>
    /// Converts the hosted intrinsic content width into the final intrinsic task-card width consumed by graph layout.
    /// </summary>
    protected override double ComputeIntrinsicNodeWidth(double intrinsicContentWidth)
    {
        return Math.Max(ExecutionGraphLayoutSettings.NodeMinWidth, intrinsicContentWidth);
    }
}
