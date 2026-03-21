using System;
using LocalAutomation.Avalonia.Bootstrap;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Starts the LocalAutomation Avalonia desktop host using bundled extension discovery.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the Avalonia desktop lifetime.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        AvaloniaAppBootstrapper.Run(args);
    }
}
