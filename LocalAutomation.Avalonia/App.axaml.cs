using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LocalAutomation.Avalonia.Controls;
using LocalAutomation.Avalonia.Diagnostics;
using AvaloniaApplication = Avalonia.Application;
using LocalAutomation.Avalonia.Bootstrap;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Wires the Avalonia application lifetime to a placeholder main window so the new shell can coexist with the
/// legacy WPF app during the migration.
/// </summary>
public partial class App : AvaloniaApplication
{
    /// <summary>
    /// Gets the launcher-provided shell identity values that control product naming and host-owned storage locations.
    /// </summary>
    public static ShellIdentity ShellIdentity { get; private set; } = ShellIdentity.LocalAutomation;

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
    /// Replaces the current launcher-provided shell identity before the shell initializes any windows or host-owned files.
    /// </summary>
    public static void ConfigureShellIdentity(ShellIdentity shellIdentity)
    {
        ShellIdentity = shellIdentity ?? throw new System.ArgumentNullException(nameof(shellIdentity));
    }

    /// <summary>
    /// Loads the application XAML resources and theme definitions.
    /// </summary>
    public override void Initialize()
    {
        PropertyGridTypeDescriptorRegistrar.Register();
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Creates the initial desktop window for the placeholder shell.
    /// </summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            PerformanceTelemetryListener.Start(Services.ApplicationSettings.EnablePerformanceTelemetry);
            MainWindow mainWindow = new();
            mainWindow.Title = ShellIdentity.WindowTitle;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
