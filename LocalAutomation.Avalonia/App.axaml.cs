using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AvaloniaApplication = Avalonia.Application;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Wires the Avalonia application lifetime to a placeholder main window so the new shell can coexist with the
/// legacy WPF app during the migration.
/// </summary>
public partial class App : AvaloniaApplication
{
    /// <summary>
    /// Gets the startup discovery warning shown by the shell when bundled extensions were missing or failed to load.
    /// </summary>
    public static string StartupMessage { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the application host configured by the outer launcher. The generic shell keeps this as mutable startup
    /// state so different launcher executables can compose different compile-time extension sets without changing the
    /// shell assembly itself.
    /// </summary>
    public static LocalAutomationApplicationHost Services { get; private set; } = LocalAutomationApplicationHost.Create();

    /// <summary>
    /// Replaces the current launcher-provided application host.
    /// </summary>
    public static void ConfigureServices(LocalAutomationApplicationHost services)
    {
        Services = services ?? throw new System.ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// Replaces the current startup discovery message shown by the shell.
    /// </summary>
    public static void ConfigureStartupMessage(string message)
    {
        StartupMessage = message ?? string.Empty;
    }

    /// <summary>
    /// Loads the application XAML resources and theme definitions.
    /// </summary>
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Creates the initial desktop window for the placeholder shell.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
