using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Hosts the first parity-focused Avalonia shell backed by the shared LocalAutomation application services.
/// </summary>
public partial class MainWindow : Window
{
    private const double AutoScrollTolerance = 4;

    private bool _shouldAutoScrollRuntimeLog = true;
    private INotifyCollectionChanged? _currentRuntimeLogCollection;

    /// <summary>
    /// Initializes the Avalonia shell window and connects it to the shared LocalAutomation view model.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(App.Services);
        ViewModel.PropertyChanged += HandleViewModelPropertyChanged;
        AttachRuntimeLogCollection();
        Closed += HandleClosed;
    }

    /// <summary>
    /// Gets the strongly typed view model for the shell window.
    /// </summary>
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>
    /// Resolves the runtime log scroll viewer used for conditional auto-follow behavior.
    /// </summary>
    private ScrollViewer? RuntimeLogViewer => this.FindControl<ScrollViewer>("RuntimeLogScrollViewer");

    /// <summary>
    /// Opens a folder picker from the compact targets-panel add button so the shell no longer needs a dedicated
    /// top-level target-creation strip.
    /// </summary>
    private async void BrowseTarget_Click(object? sender, RoutedEventArgs e)
    {
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            ViewModel.SetStatus("Folder browsing is not available in this window.");
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select target folder",
            AllowMultiple = false
        });

        IStorageFolder? selectedFolder = folders.Count > 0 ? folders[0] : null;
        if (selectedFolder == null)
        {
            return;
        }

        string? path = selectedFolder.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            ViewModel.SetStatus("The selected folder is not available as a local filesystem path.");
            return;
        }

        ViewModel.NewTargetPath = path;
        if (!ViewModel.TryAddTargetFromInput(out string? errorMessage) && !string.IsNullOrWhiteSpace(errorMessage))
        {
            ViewModel.SetStatus(errorMessage);
        }
    }

    /// <summary>
    /// Removes the currently selected target from the list-level action inside the target card.
    /// </summary>
    private void RemoveSelectedTarget_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedTarget == null)
        {
            return;
        }

        ViewModel.RemoveSelectedTarget();
    }

    /// <summary>
    /// Copies the current primary command preview text onto the clipboard when one is available.
    /// </summary>
    private async void CopyCommand_Click(object? sender, RoutedEventArgs e)
    {
        string? commandText = ViewModel.PrimaryCommandText;
        if (string.IsNullOrWhiteSpace(commandText))
        {
            ViewModel.SetStatus("No command is available to copy yet.");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
        {
            ViewModel.SetStatus("Clipboard access is not available in this window.");
            return;
        }

        await clipboard.SetTextAsync(commandText);
        ViewModel.SetStatus("Copied the current command preview to the clipboard.");
    }

    /// <summary>
    /// Starts the selected operation from the Avalonia shell.
    /// </summary>
    private void Execute_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.Execute();
    }

    /// <summary>
    /// Cancels the current execution session when one is active.
    /// </summary>
    private async void Terminate_Click(object? sender, RoutedEventArgs e)
    {
        await ViewModel.CancelExecutionAsync();
    }

    /// <summary>
    /// Executes a selected target action from the selected-target action strip.
    /// </summary>
    private void TargetAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TargetContextActionViewModel action })
        {
            return;
        }

        ViewModel.ExecuteTargetAction(action);
    }

    /// <summary>
    /// Selects a runtime tab in the global runtime panel.
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
    /// Tracks whether the runtime log view is currently pinned to the bottom so new lines only auto-follow when the
    /// user has not intentionally scrolled upward.
    /// </summary>
    private void RuntimeLogScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _shouldAutoScrollRuntimeLog = IsRuntimeLogAtBottom();
    }

    /// <summary>
    /// Recomputes the responsive options column count whenever the options viewport width changes.
    /// </summary>
    private void OptionsScrollViewer_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ViewModel.UpdateOptionsColumnCount(e.NewSize.Width);
    }

    /// <summary>
    /// Flushes any pending debounced session save before the window fully closes.
    /// </summary>
    private void HandleClosed(object? sender, System.EventArgs e)
    {
        ViewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        DetachRuntimeLogCollection();
        ViewModel.FlushPendingSessionState();
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
}
