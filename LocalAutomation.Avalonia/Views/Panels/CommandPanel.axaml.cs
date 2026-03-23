using Avalonia.Controls;
using Avalonia.Interactivity;
using LocalAutomation.Avalonia.ViewModels;

namespace LocalAutomation.Avalonia.Views.Panels;

/// <summary>
/// Hosts the command preview and command actions.
/// </summary>
public partial class CommandPanel : UserControl
{
    /// <summary>
    /// Initializes the command panel.
    /// </summary>
    public CommandPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets the shared shell view model backing this panel.
    /// </summary>
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>
    /// Copies the current command preview to the clipboard.
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
    /// Starts the selected operation from the shell.
    /// </summary>
    private void Execute_Click(object? sender, RoutedEventArgs e)
    {
        ViewModel.Execute();
    }
}
