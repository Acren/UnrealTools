using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LocalAutomation.Avalonia.ViewModels;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders one execution-graph task card so graph canvas code only needs to place it and connect graph interactions.
/// </summary>
public partial class ExecutionTaskCard : UserControl
{
    private bool _isHovered;
    private bool _isPressed;
    private ExecutionNodeViewModel? _observedNode;

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

        Border clickSurface = GetRequiredBorder("ClickSurface");
        clickSurface.PointerEntered += ClickSurface_PointerEntered;
        clickSurface.PointerExited += ClickSurface_PointerExited;
        clickSurface.PointerPressed += ClickSurface_PointerPressed;
        clickSurface.PointerReleased += ClickSurface_PointerReleased;

        DataContextChanged += HandleDataContextChanged;
    }

    /// <summary>
    /// Raised when the task card is clicked with the pointer.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? Invoked;

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
    /// Applies the hover class while the pointer is over the card hit surface.
    /// </summary>
    private void ClickSurface_PointerEntered(object? sender, PointerEventArgs e)
    {
        _isHovered = true;
        ApplySemanticClasses();
    }

    /// <summary>
    /// Clears transient interaction classes once the pointer leaves the card.
    /// </summary>
    private void ClickSurface_PointerExited(object? sender, PointerEventArgs e)
    {
        _isHovered = false;
        _isPressed = false;
        ApplySemanticClasses();
    }

    /// <summary>
    /// Marks the card as pressed on primary-button down so the graph can provide immediate pointer feedback.
    /// </summary>
    private void ClickSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Border clickSurface = GetRequiredBorder("ClickSurface");
        if (!e.GetCurrentPoint(clickSurface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPressed = true;
        ApplySemanticClasses();
        e.Handled = true;
    }

    /// <summary>
    /// Raises the custom invoked event when the primary-button release lands on the card hit surface.
    /// </summary>
    private void ClickSurface_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        Border clickSurface = GetRequiredBorder("ClickSurface");
        bool isInsideCard = clickSurface.Bounds.Contains(e.GetPosition(clickSurface));
        _isPressed = false;
        ApplySemanticClasses();
        if (!isInsideCard || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        Invoked?.Invoke(this, new RoutedEventArgs());
        e.Handled = true;
    }

    /// <summary>
    /// Looks up one named border from the compiled XAML so pointer handlers can manipulate the visual shell directly.
    /// </summary>
    private Border GetRequiredBorder(string name)
    {
        return this.FindControl<Border>(name)
            ?? throw new InvalidOperationException($"ExecutionTaskCard border '{name}' was not initialized.");
    }

    /// <summary>
    /// Looks up the dedicated animated border control that owns the reusable conic border animation.
    /// </summary>
    private AnimatedConicBorder GetRequiredAnimatedBorder(string name)
    {
        return this.FindControl<AnimatedConicBorder>(name)
            ?? throw new InvalidOperationException($"ExecutionTaskCard animated border '{name}' was not initialized.");
    }

    /// <summary>
    /// Rebinds property-change observation when the control starts rendering a different node view model.
    /// </summary>
    private void HandleDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedNode != null)
        {
            _observedNode.PropertyChanged -= HandleObservedNodePropertyChanged;
        }

        _observedNode = DataContext as ExecutionNodeViewModel;
        if (_observedNode != null)
        {
            _observedNode.PropertyChanged += HandleObservedNodePropertyChanged;
        }

        ApplySemanticClasses();
    }

    /// <summary>
    /// Reapplies semantic classes whenever the node's status or selection state changes.
    /// </summary>
    private void HandleObservedNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.State), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.Status), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.DisplayStatus), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.IsSelected), StringComparison.Ordinal))
        {
            ApplySemanticClasses();
        }
    }

    /// <summary>
    /// Applies the task-card semantic classes consumed by the local Avalonia styles and forwards whether the extracted
    /// animated border control should be active for the current task status.
    /// </summary>
    private void ApplySemanticClasses()
    {
        if (_observedNode == null)
        {
            return;
        }

        Border backgroundShell = GetRequiredBorder("CardBackgroundShell");
        Border borderChrome = GetRequiredBorder("CardBorderChrome");
        AnimatedConicBorder animatedBorderChrome = GetRequiredAnimatedBorder("AnimatedBorderChrome");

        ExecutionStatusClasses.ApplyInteractionClasses(backgroundShell.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyInteractionClasses(borderChrome.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(borderChrome.Classes, _observedNode.DisplayStatus);
        ExecutionStatusClasses.ApplyStatusClasses(animatedBorderChrome.Classes, _observedNode.DisplayStatus);

        /* Let the extracted control own the timer and conic brush creation while the task card only forwards whether the
           current task status should display the animated accent treatment. */
        animatedBorderChrome.IsAnimated = _observedNode.State == global::LocalAutomation.Runtime.ExecutionTaskState.Running;
    }
}
