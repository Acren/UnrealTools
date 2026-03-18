using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LocalAutomation.Extensions.Unreal;
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
    /// Keeps the compile-time application host alive for the duration of the app so later phases can consume shared
    /// extension-backed services from a single source.
    /// </summary>
    public static LocalAutomationApplicationHost Services { get; } = CreateApplicationHost();

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

    /// <summary>
    /// Registers compile-time extension modules for the current app session and exposes the resulting services.
    /// </summary>
    private static LocalAutomationApplicationHost CreateApplicationHost()
    {
        return LocalAutomationApplicationHost.Create(new UnrealExtensionModule());
    }
}
