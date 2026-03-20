using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Views.Panels;

/// <summary>
/// Hosts runtime tabs and the shared runtime log output.
/// </summary>
public partial class RuntimePanel : UserControl
{
    private const double AutoScrollTolerance = 4;

    private bool _shouldAutoScrollRuntimeLog = true;
    private INotifyCollectionChanged? _currentRuntimeLogCollection;
    private MainWindowViewModel? _observedViewModel;

    /// <summary>
    /// Initializes the runtime panel.
    /// </summary>
    public RuntimePanel()
    {
        InitializeComponent();
        AttachedToVisualTree += HandleAttachedToVisualTree;
        DetachedFromVisualTree += HandleDetachedFromVisualTree;
        DataContextChanged += HandleDataContextChanged;
    }

    /// <summary>
    /// Gets the shared shell view model backing this panel.
    /// </summary>
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>
    /// Resolves the runtime log scroll viewer used for conditional auto-follow behavior.
    /// </summary>
    private ScrollViewer? RuntimeLogViewer => this.FindControl<ScrollViewer>("RuntimeLogScrollViewer");

    /// <summary>
    /// Resolves the single selectable log surface used for multi-line selection.
    /// </summary>
    private SelectableTextBlock? RuntimeLogSelectionSurface => this.FindControl<SelectableTextBlock>("RuntimeLogTextBlock");

    /// <summary>
    /// Selects a runtime tab.
    /// </summary>
    private void RuntimeTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RuntimeTaskTabViewModel runtimeTab })
        {
            return;
        }

        ViewModel.SelectedRuntimeTab = runtimeTab;
    }

    /// <summary>
    /// Cancels the current execution session when one is active.
    /// </summary>
    private async void Terminate_Click(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CancelExecutionAsync();
    }

    /// <summary>
    /// Clears the currently selected runtime log from the context menu.
    /// </summary>
    private void ClearLog_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelectedRuntimeLog();
        RenderEntireRuntimeLog();
        _shouldAutoScrollRuntimeLog = true;
    }

    /// <summary>
    /// Tracks whether the runtime log is pinned to the bottom.
    /// </summary>
    private void RuntimeLogScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _shouldAutoScrollRuntimeLog = IsRuntimeLogAtBottom();
    }

    /// <summary>
    /// Rewires runtime-log subscriptions when the view model changes.
    /// </summary>
    private void HandleDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        }

        _observedViewModel = DataContext as MainWindowViewModel;
        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged += HandleViewModelPropertyChanged;
            AttachRuntimeLogCollection();
            return;
        }

        DetachRuntimeLogCollection();
    }

    /// <summary>
    /// Hooks runtime log updates once the panel is on screen.
    /// </summary>
    private void HandleAttachedToVisualTree(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is MainWindowViewModel)
        {
            AttachRuntimeLogCollection();
        }
    }

    /// <summary>
    /// Cleans up runtime log subscriptions when the panel leaves the visual tree.
    /// </summary>
    private void HandleDetachedFromVisualTree(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_observedViewModel != null)
        {
            _observedViewModel.PropertyChanged -= HandleViewModelPropertyChanged;
            _observedViewModel = null;
        }

        DetachRuntimeLogCollection();
    }

    /// <summary>
    /// Rewires runtime-log auto-follow whenever the selected runtime tab changes.
    /// </summary>
    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MainWindowViewModel.SelectedRuntimeLogEntries), StringComparison.Ordinal))
        {
            return;
        }

        AttachRuntimeLogCollection();
        RenderEntireRuntimeLog();
        _shouldAutoScrollRuntimeLog = true;
        ScrollRuntimeLogToEnd();
    }

    /// <summary>
    /// Attaches to the selected runtime log collection so new entries can trigger conditional auto-scroll.
    /// </summary>
    private void AttachRuntimeLogCollection()
    {
        DetachRuntimeLogCollection();
        _currentRuntimeLogCollection = ViewModel.SelectedRuntimeLogEntries;
        _currentRuntimeLogCollection.CollectionChanged += HandleRuntimeLogCollectionChanged;
        RenderEntireRuntimeLog();
    }

    /// <summary>
    /// Detaches from the previously selected runtime log collection.
    /// </summary>
    private void DetachRuntimeLogCollection()
    {
        if (_currentRuntimeLogCollection == null)
        {
            return;
        }

        _currentRuntimeLogCollection.CollectionChanged -= HandleRuntimeLogCollectionChanged;
        _currentRuntimeLogCollection = null;
    }

    /// <summary>
    /// Auto-scrolls newly appended runtime output only when the user is already at the bottom of the log.
    /// </summary>
    private void HandleRuntimeLogCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (object? item in e.NewItems)
            {
                if (item is LogEntryViewModel entry)
                {
                    AppendRuntimeLogEntry(entry);
                }
            }
        }
        else
        {
            RenderEntireRuntimeLog();
        }

        if (!_shouldAutoScrollRuntimeLog || e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        ScrollRuntimeLogToEnd();
    }

    /// <summary>
    /// Scrolls the runtime log viewer to the end on the next UI tick.
    /// </summary>
    private void ScrollRuntimeLogToEnd()
    {
        Dispatcher.UIThread.Post(() => RuntimeLogViewer?.ScrollToEnd());
    }

    /// <summary>
    /// Returns whether the runtime log viewer is currently close enough to the bottom to keep auto-follow enabled.
    /// </summary>
    private bool IsRuntimeLogAtBottom()
    {
        ScrollViewer? runtimeLogViewer = RuntimeLogViewer;
        if (runtimeLogViewer == null)
        {
            return true;
        }

        double remaining = runtimeLogViewer.Extent.Height - runtimeLogViewer.Viewport.Height - runtimeLogViewer.Offset.Y;
        return remaining <= AutoScrollTolerance;
    }

    /// <summary>
    /// Rebuilds the full selectable runtime log surface for the selected tab.
    /// </summary>
    private void RenderEntireRuntimeLog()
    {
        SelectableTextBlock? logTextBlock = RuntimeLogSelectionSurface;
        if (logTextBlock == null)
        {
            return;
        }

        logTextBlock.Inlines?.Clear();
        foreach (LogEntryViewModel entry in ViewModel.SelectedRuntimeLogEntries)
        {
            AppendRuntimeLogEntry(entry);
        }
    }

    /// <summary>
    /// Appends one colored line to the shared selectable runtime log surface.
    /// </summary>
    private void AppendRuntimeLogEntry(LogEntryViewModel entry)
    {
        SelectableTextBlock? logTextBlock = RuntimeLogSelectionSurface;
        if (logTextBlock == null)
        {
            return;
        }

        InlineCollection inlines = logTextBlock.Inlines ??= new InlineCollection();
        if (inlines.Count > 0)
        {
            inlines.Add(new LineBreak());
        }

        // A muted fixed-width timestamp makes long-running logs easier to scan without overpowering severity colors.
        inlines.Add(new Run
        {
            Text = $"[{entry.TimestampText}] ",
            Foreground = new SolidColorBrush(Color.Parse("#8B949E"))
        });

        inlines.Add(new Run
        {
            Text = entry.Message,
            Foreground = new SolidColorBrush(Color.Parse(entry.Foreground))
        });
    }
}
