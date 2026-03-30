using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LocalAutomation.Avalonia.Controls;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Views.Panels;

/// <summary>
/// Hosts runtime tabs and keeps the selected runtime log pinned to the bottom when the user is already following the
/// live output stream.
/// </summary>
public partial class ExecutionWorkspacePanel : UserControl
{
    private const double AutoScrollTolerance = 4;

    private bool _shouldAutoScrollRuntimeLog = true;
    private INotifyCollectionChanged? _currentRuntimeLogCollection;
    private ExecutionWorkspaceViewModel? _observedViewModel;

    /// <summary>
    /// Initializes the runtime panel.
    /// </summary>
    public ExecutionWorkspacePanel()
    {
        InitializeComponent();
        AttachedToVisualTree += HandleAttachedToVisualTree;
        DetachedFromVisualTree += HandleDetachedFromVisualTree;
        DataContextChanged += HandleDataContextChanged;
    }

    /// <summary>
    /// Gets the runtime panel view model backing this panel.
    /// </summary>
    private ExecutionWorkspaceViewModel ViewModel => (ExecutionWorkspaceViewModel)DataContext!;

    /// <summary>
    /// Selects a runtime tab.
    /// </summary>
    private void RuntimeTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RuntimeWorkspaceTabViewModel runtimeTab })
        {
            return;
        }

        ViewModel.SelectedRuntimeTab = runtimeTab;
    }

    /// <summary>
    /// Lets users close closable runtime tabs with a middle click while keeping the permanent app log tab protected.
    /// </summary>
    private void RuntimeTab_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        if (sender is not Control { DataContext: RuntimeWorkspaceTabViewModel runtimeTab } || !runtimeTab.CanClose)
        {
            return;
        }

        _ = ViewModel.CloseRuntimeTabAsync(runtimeTab);
        e.Handled = true;
    }

    /// <summary>
    /// Closes a runtime task tab from the tab strip, cancelling active work first when needed.
    /// </summary>
    private void CloseRuntimeTab_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RuntimeWorkspaceTabViewModel runtimeTab })
        {
            return;
        }

        _ = ViewModel.CloseRuntimeTabAsync(runtimeTab);
        e.Handled = true;
    }

    /// <summary>
    /// Cancels the current execution session when one is active.
    /// </summary>
    private async void Terminate_Click(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CancelExecutionAsync();
    }

    /// <summary>
    /// Clears the currently selected runtime log from the context menu and resets follow-tail behavior.
    /// </summary>
    private void ClearLog_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.ClearSelectedRuntimeLog();
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

        _observedViewModel = DataContext as ExecutionWorkspaceViewModel;
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
        if (DataContext is ExecutionWorkspaceViewModel)
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
        if (!string.Equals(e.PropertyName, nameof(ExecutionWorkspaceViewModel.SelectedRuntimeLogEntries), StringComparison.Ordinal))
        {
            return;
        }

        if (ReferenceEquals(_currentRuntimeLogCollection, ViewModel.SelectedRuntimeLogEntries))
        {
            return;
        }

        AttachRuntimeLogCollection();
        _shouldAutoScrollRuntimeLog = true;
        ScrollRuntimeLogToEnd();
    }

    /// <summary>
    /// Attaches to the selected runtime log collection so appends can trigger conditional auto-follow.
    /// </summary>
    private void AttachRuntimeLogCollection()
    {
        DetachRuntimeLogCollection();
        _currentRuntimeLogCollection = ViewModel.SelectedRuntimeLogEntries;
        _currentRuntimeLogCollection.CollectionChanged += HandleRuntimeLogCollectionChanged;
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
        if (!_shouldAutoScrollRuntimeLog || e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        ScrollRuntimeLogToEnd();
    }

    /// <summary>
    /// Scrolls the runtime log viewer to the end on the next UI tick after the repeater has realized the latest rows.
    /// </summary>
    private void ScrollRuntimeLogToEnd()
    {
        Dispatcher.UIThread.Post(() => (this.FindControl<ScrollViewer>("RuntimeLogScrollViewer") ?? this.FindControl<ScrollViewer>("RuntimeLogScrollViewerApp"))?.ScrollToEnd());
    }

    /// <summary>
    /// Routes graph-node clicks from the embedded graph control into the runtime workspace selection model.
    /// </summary>
    private void GraphNode_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ExecutionWorkspaceViewModel viewModel ||
            viewModel.SelectedRuntimeTab == null ||
            sender is not Control { DataContext: ExecutionNodeViewModel node })
        {
            return;
        }

        viewModel.SelectGraphNode(viewModel.SelectedRuntimeTab, node);
        e.Handled = true;
    }

    /// <summary>
    /// Returns whether the runtime log viewer is currently close enough to the bottom to keep auto-follow enabled.
    /// </summary>
    private bool IsRuntimeLogAtBottom()
    {
        ScrollViewer? runtimeLogViewer = this.FindControl<ScrollViewer>("RuntimeLogScrollViewer") ?? this.FindControl<ScrollViewer>("RuntimeLogScrollViewerApp");
        if (runtimeLogViewer == null)
        {
            return true;
        }

        double remaining = runtimeLogViewer.Extent.Height - runtimeLogViewer.Viewport.Height - runtimeLogViewer.Offset.Y;
        return remaining <= AutoScrollTolerance;
    }
}
