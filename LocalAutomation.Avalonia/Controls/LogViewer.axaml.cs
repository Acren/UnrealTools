using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Hosts the scroll viewer and viewport for one log source, keeping follow-tail behavior local to the log-viewing
/// experience instead of leaking that state into workspace panels.
/// </summary>
public partial class LogViewer : UserControl
{
    private const double AutoScrollTolerance = 4;

    private bool _shouldAutoScroll = true;
    private INotifyCollectionChanged? _observedEntriesCollection;

    /// <summary>
    /// Identifies the log entries rendered by the embedded viewport.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyList<LogEntryViewModel>?> EntriesProperty =
        AvaloniaProperty.Register<LogViewer, IReadOnlyList<LogEntryViewModel>?>(nameof(Entries));

    /// <summary>
    /// Identifies the logical log source currently shown by the viewer. The source key changes only when the user
    /// switches to a different log stream, which allows the viewer to distinguish a real source switch from ordinary
    /// append activity or collection refreshes.
    /// </summary>
    public static readonly StyledProperty<string?> SourceKeyProperty =
        AvaloniaProperty.Register<LogViewer, string?>(nameof(SourceKey));

    /// <summary>
    /// Creates the reusable log-viewer control.
    /// </summary>
    public LogViewer()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the log entries rendered by the embedded viewport.
    /// </summary>
    public IReadOnlyList<LogEntryViewModel>? Entries
    {
        get => GetValue(EntriesProperty);
        set => SetValue(EntriesProperty, value);
    }

    /// <summary>
    /// Gets or sets the logical source identity for the currently displayed log stream.
    /// </summary>
    public string? SourceKey
    {
        get => GetValue(SourceKeyProperty);
        set => SetValue(SourceKeyProperty, value);
    }

    /// <summary>
    /// Rewires collection listeners and source-follow behavior whenever the bound log entries or their logical source
    /// changes.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == EntriesProperty)
        {
            AttachEntriesCollection(change.GetNewValue<IReadOnlyList<LogEntryViewModel>?>());

            // A collection replacement for the same source is just a content refresh. Keep the user's follow-tail
            // preference, but if they were already following the tail, preserve that behavior after the refresh.
            if (_shouldAutoScroll)
            {
                ScrollToEnd();
            }

            return;
        }

        if (change.Property == SourceKeyProperty)
        {
            HandleSourceKeyChanged(change.GetOldValue<string?>(), change.GetNewValue<string?>());
        }
    }

    /// <summary>
    /// Drops collection listeners when the control leaves the visual tree.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        AttachEntriesCollection(null);
    }

    /// <summary>
    /// Tracks whether the user is currently following the log tail.
    /// </summary>
    private void ScrollHost_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _shouldAutoScroll = IsAtBottom();
    }

    /// <summary>
    /// Resets follow-tail only when the logical log source really changes, which is the user's expected behavior when
    /// switching tabs or selecting a different task.
    /// </summary>
    private void HandleSourceKeyChanged(string? previousSourceKey, string? currentSourceKey)
    {
        if (string.Equals(previousSourceKey, currentSourceKey, StringComparison.Ordinal))
        {
            return;
        }

        _shouldAutoScroll = true;
        ScrollToEnd();
    }

    /// <summary>
    /// Attaches to the active entry collection so incremental appends can continue follow-tail behavior when the user is
    /// already at the bottom of the current source.
    /// </summary>
    private void AttachEntriesCollection(IReadOnlyList<LogEntryViewModel>? entries)
    {
        if (_observedEntriesCollection != null)
        {
            _observedEntriesCollection.CollectionChanged -= HandleEntriesCollectionChanged;
            _observedEntriesCollection = null;
        }

        _observedEntriesCollection = entries as INotifyCollectionChanged;
        if (_observedEntriesCollection != null)
        {
            _observedEntriesCollection.CollectionChanged += HandleEntriesCollectionChanged;
        }
    }

    /// <summary>
    /// Auto-scrolls only for true append activity while follow-tail is enabled. Manual upward scrolling leaves
    /// follow-tail off until the user returns to the bottom or switches sources.
    /// </summary>
    private void HandleEntriesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_shouldAutoScroll || e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        ScrollToEnd();
    }

    /// <summary>
    /// Scrolls to the end on the next UI tick so the scroll viewer sees the latest realized viewport size.
    /// </summary>
    private void ScrollToEnd()
    {
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("ScrollHost")?.ScrollToEnd());
    }

    /// <summary>
    /// Returns whether the current scroll position is close enough to the end to count as follow-tail mode.
    /// </summary>
    private bool IsAtBottom()
    {
        ScrollViewer? scrollHost = this.FindControl<ScrollViewer>("ScrollHost");
        if (scrollHost == null)
        {
            return true;
        }

        double remaining = scrollHost.Extent.Height - scrollHost.Viewport.Height - scrollHost.Offset.Y;
        return remaining <= AutoScrollTolerance;
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the log viewer.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
