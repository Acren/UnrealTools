using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Views.Panels;

/// <summary>
/// Hosts target selection, target management, and target-scoped actions.
/// </summary>
public partial class TargetPanel : UserControl
{
    /// <summary>
    /// Initializes the target panel.
    /// </summary>
    public TargetPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets the target panel view model backing this panel.
    /// </summary>
    private TargetPanelViewModel ViewModel => (TargetPanelViewModel)DataContext!;

    /// <summary>
    /// Opens the folder picker when the synthetic add-target row is selected.
    /// </summary>
    private async void TargetPicker_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || comboBox.SelectedItem is not TargetPickerItemViewModel pickerItem || !pickerItem.IsAddAction)
        {
            return;
        }

        comboBox.SelectedItem = ViewModel.SelectedTargetPickerItem;

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
    /// Removes a target directly from the dropdown row.
    /// </summary>
    private void RemoveTargetPickerItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TargetPickerItemViewModel { TargetItem: { } targetItem } })
        {
            return;
        }

        e.Handled = true;

        ViewModel.RemoveTarget(targetItem);
    }

    /// <summary>
    /// Executes a selected target action.
    /// </summary>
    private void TargetAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: TargetContextActionViewModel action })
        {
            return;
        }

        ViewModel.ExecuteTargetAction(action);
    }
}
