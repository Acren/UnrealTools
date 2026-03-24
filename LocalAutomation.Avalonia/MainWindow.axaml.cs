using Avalonia.Controls;
using LocalAutomation.Avalonia.ViewModels;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Hosts the composed Avalonia shell and wires the shared view model to the extracted panel views.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Initializes the shell window and assigns the shared main-window view model.
    /// </summary>
    public MainWindow(LocalAutomationApplicationHost services)
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel(services);
        Closed += HandleClosed;
    }

    /// <summary>
    /// Preserves the XAML loader entry point while delegating real composition to the launcher-provided host.
    /// </summary>
    public MainWindow()
        : this(App.Services)
    {
    }

    /// <summary>
    /// Gets the strongly typed view model for the shell window.
    /// </summary>
    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    /// <summary>
    /// Flushes any pending debounced session save before the window fully closes.
    /// </summary>
    private void HandleClosed(object? sender, System.EventArgs e)
    {
        Closed -= HandleClosed;
        ViewModel.FlushPendingSessionState();
    }

}
