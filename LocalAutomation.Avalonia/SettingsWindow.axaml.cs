using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LocalAutomation.Avalonia.ViewModels;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Hosts the global application settings editor for the current launcher.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>
    /// Creates the settings window around the shared application host.
    /// </summary>
    public SettingsWindow(LocalAutomationApplicationHost services)
    {
        InitializeComponent();
        DataContext = new SettingsWindowViewModel(services);
        Closed += HandleClosed;
    }

    /// <summary>
    /// Preserves the XAML loader constructor for designer or framework activation.
    /// </summary>
    public SettingsWindow()
        : this(App.Services)
    {
    }

    /// <summary>
    /// Gets the strongly typed view model backing the settings window.
    /// </summary>
    private SettingsWindowViewModel ViewModel => (SettingsWindowViewModel)DataContext!;

    /// <summary>
    /// Closes the settings window from the footer action row.
    /// </summary>
    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// Flushes pending saves and detaches from the shared settings object before the window is released.
    /// </summary>
    private void HandleClosed(object? sender, System.EventArgs e)
    {
        Closed -= HandleClosed;
        ViewModel.FlushPendingSave();
        ViewModel.Dispose();
    }

    /// <summary>
    /// Loads the compiled Avalonia markup for the settings window.
    /// </summary>
    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
