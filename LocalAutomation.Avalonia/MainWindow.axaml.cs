using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Hosts the first parity-focused Avalonia shell backed by the shared LocalAutomation application services.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Initializes the Avalonia shell window and connects it to the shared LocalAutomation view model.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(App.Services);
    }

    /// <summary>
    /// Gets the strongly typed view model for the shell window.
    /// </summary>
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>
    /// Adds a target from the current input path and surfaces any validation error through the shared status text.
    /// </summary>
    private void AddTarget_Click(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.TryAddTargetFromInput(out string? errorMessage) && !string.IsNullOrWhiteSpace(errorMessage))
        {
            ViewModel.SetStatus(errorMessage);
        }
    }

    /// <summary>
    /// Opens a folder picker so the Avalonia shell matches the legacy target-selection flow.
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
        ViewModel.SetStatus($"Selected target folder '{path}'.");
    }

    /// <summary>
    /// Removes the currently selected target from the in-memory Avalonia session.
    /// </summary>
    private void RemoveTarget_Click(object? sender, RoutedEventArgs e)
    {
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
}
