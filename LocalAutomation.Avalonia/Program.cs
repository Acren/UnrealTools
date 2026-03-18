using Avalonia;
using System;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Boots the Avalonia desktop shell so Phase 1 has a runnable placeholder app while the architecture is
/// extracted in later phases.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the Avalonia desktop lifetime for the LocalAutomation shell.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Configures the shared Avalonia application builder used by the desktop entry point.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
