using System;
using LocalAutomation.Avalonia.Bootstrap;

namespace LocalAutomation.Avalonia;

/// <summary>
/// Starts the LocalAutomation desktop shell using bundled extension discovery.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the desktop lifetime.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        ShellAppBootstrapper.Run(args);
    }
}
