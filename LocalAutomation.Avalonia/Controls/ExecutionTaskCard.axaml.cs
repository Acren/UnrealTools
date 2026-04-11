using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LocalAutomation.Avalonia.ExecutionGraph;
using LocalAutomation.Avalonia.ViewModels;
using LocalAutomation.Core;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Renders one execution-graph task card so graph canvas code only needs to place it and connect graph interactions.
/// </summary>
public partial class ExecutionTaskCard : UserControl
{
    private static readonly Thickness SelectedBorderThickness = new(2.0);
    private static readonly Thickness DefaultBorderThickness = new(1.5);
    private bool _isHovered;
    private bool _isPressed;
    private bool _subscriptionsAttached;
    private ExecutionNodeViewModel? _observedNode;
    private double _intrinsicNodeWidth = ExecutionGraphLayoutSettings.NodeMinWidth;
    private IntrinsicSizeHost? _contentIntrinsicHost;

    /// <summary>
    /// Identifies the rendered height for the task card.
    /// </summary>
    public static readonly StyledProperty<double> CardHeightProperty =
        AvaloniaProperty.Register<ExecutionTaskCard, double>(nameof(CardHeight));

    /// <summary>
    /// Identifies the current border thickness used by the task card chrome and animated overlay.
    /// </summary>
    public static readonly DirectProperty<ExecutionTaskCard, Thickness> FrameBorderThicknessProperty =
        AvaloniaProperty.RegisterDirect<ExecutionTaskCard, Thickness>(
            nameof(FrameBorderThickness),
            control => control.FrameBorderThickness);

    /// <summary>
    /// Identifies the card's current intrinsic width resolved from its real visible content.
    /// </summary>
    public static readonly DirectProperty<ExecutionTaskCard, double> IntrinsicNodeWidthProperty =
        AvaloniaProperty.RegisterDirect<ExecutionTaskCard, double>(
            nameof(IntrinsicNodeWidth),
            control => control.IntrinsicNodeWidth);

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
        AttachedToVisualTree += HandleAttachedToVisualTree;
        DetachedFromVisualTree += HandleDetachedFromVisualTree;
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
    /// Gets the current border thickness for the task card chrome and animated border overlay.
    /// </summary>
    public Thickness FrameBorderThickness => _observedNode?.IsSelected == true
        ? SelectedBorderThickness
        : DefaultBorderThickness;

    /// <summary>
    /// Gets the card's current intrinsic width resolved from its real visible content.
    /// </summary>
    public double IntrinsicNodeWidth
    {
        get => _intrinsicNodeWidth;
        private set => SetAndRaise(IntrinsicNodeWidthProperty, ref _intrinsicNodeWidth, value);
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the task card.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _contentIntrinsicHost = this.FindControl<IntrinsicSizeHost>("ContentIntrinsicHost")
            ?? throw new InvalidOperationException("ExecutionTaskCard intrinsic content host was not initialized.");
        ((INotifyPropertyChanged)_contentIntrinsicHost).PropertyChanged += HandleContentIntrinsicHostPropertyChanged;
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
        Thickness previousThickness = FrameBorderThickness;
        DetachObservedNodeSubscriptions();
        _observedNode = DataContext as ExecutionNodeViewModel;
        AttachObservedNodeSubscriptions();

        ApplySemanticClasses();
        UpdateIntrinsicNodeWidthIfNeeded();
        RaisePropertyChanged(FrameBorderThicknessProperty, previousThickness, FrameBorderThickness);
    }

    /// <summary>
    /// Restores view-model subscriptions when a retained task card re-enters the visual tree with the same data context.
    /// </summary>
    private void HandleAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachObservedNodeSubscriptions();
    }

    /// <summary>
    /// Releases task and node subscriptions when the control leaves the visual tree so removed retained controls and any
    /// other transient visual-tree instances do not stay alive through shared task-view-model event handlers.
    /// </summary>
    private void HandleDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachObservedNodeSubscriptions();
    }

    /// <summary>
    /// Reapplies selection-owned visuals whenever the graph node selection state changes.
    /// </summary>
    private void HandleObservedNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(ExecutionNodeViewModel.IsSelected), StringComparison.Ordinal))
        {
            return;
        }

        ApplySemanticClasses();
        Thickness nextThickness = FrameBorderThickness;
        Thickness previousThickness = nextThickness.Equals(SelectedBorderThickness)
            ? DefaultBorderThickness
            : SelectedBorderThickness;
        RaisePropertyChanged(FrameBorderThicknessProperty, previousThickness, nextThickness);
    }

    /// <summary>
    /// Reapplies status-owned visuals whenever the nested task view model changes in a way that affects semantic styling
    /// or the running animation state.
    /// </summary>
    private void HandleObservedTaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.State), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(ExecutionTaskViewModel.DisplayStatus), StringComparison.Ordinal))
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
        Border clickSurface = GetRequiredBorder("ClickSurface");
        AnimatedConicBorder animatedBorderChrome = GetRequiredAnimatedBorder("AnimatedBorderChrome");

        /* The outer click surface carries whole-card opacity treatment, while the inner shells still own fill and stroke
           styling. All three layers need the same semantic classes so the styles can compose predictably. */
        ExecutionStatusClasses.ApplyInteractionClasses(clickSurface.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(clickSurface.Classes, _observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyInteractionClasses(backgroundShell.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(backgroundShell.Classes, _observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyInteractionClasses(borderChrome.Classes, _observedNode.IsSelected, _isHovered, _isPressed);
        ExecutionStatusClasses.ApplyStatusClasses(borderChrome.Classes, _observedNode.Task.DisplayStatus);
        ExecutionStatusClasses.ApplyStatusClasses(animatedBorderChrome.Classes, _observedNode.Task.DisplayStatus);

        /* Let the extracted control own the timer and conic brush creation while the task card only forwards whether the
           current task status should display the animated accent treatment. */
        animatedBorderChrome.IsAnimated = _observedNode.Task.State == global::LocalAutomation.Runtime.ExecutionTaskState.Running;
    }

    /// <summary>
    /// Recomputes the intrinsic width whenever the real visible content host reports a new unconstrained desired width.
    /// </summary>
    private void HandleContentIntrinsicHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(IntrinsicSizeHost.IntrinsicWidth), StringComparison.Ordinal))
        {
            return;
        }

        UpdateIntrinsicNodeWidthIfNeeded();
    }

    /// <summary>
    /// Stores the latest intrinsic card width derived from the real visible content block.
    /// </summary>
    private void UpdateIntrinsicNodeWidthIfNeeded()
    {
        if (_contentIntrinsicHost == null)
        {
            return;
        }

        IntrinsicNodeWidth = Math.Max(ExecutionGraphLayoutSettings.NodeMinWidth, _contentIntrinsicHost.IntrinsicWidth);
    }

    /// <summary>
    /// Hooks the current node and task view models exactly once while the control is alive in the visual tree.
    /// </summary>
    private void AttachObservedNodeSubscriptions()
    {
        if (_subscriptionsAttached || _observedNode == null)
        {
            return;
        }

        _observedNode.PropertyChanged += HandleObservedNodePropertyChanged;
        _observedNode.Task.PropertyChanged += HandleObservedTaskPropertyChanged;
        _subscriptionsAttached = true;
    }

    /// <summary>
    /// Unhooks the current node and task view models when the control is detached or rebound.
    /// </summary>
    private void DetachObservedNodeSubscriptions()
    {
        if (!_subscriptionsAttached || _observedNode == null)
        {
            return;
        }

        _observedNode.PropertyChanged -= HandleObservedNodePropertyChanged;
        _observedNode.Task.PropertyChanged -= HandleObservedTaskPropertyChanged;
        _subscriptionsAttached = false;
    }
}
