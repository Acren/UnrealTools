using System;
using LocalAutomation.Avalonia.Bootstrap;

namespace UnrealCommander.Avalonia;

/// <summary>
/// Starts the Unreal Commander branded launcher while reusing the shared generic Avalonia shell.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the Avalonia desktop lifetime with Unreal Commander branding and isolated local state.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        AvaloniaAppBootstrapper.Run(args, UnrealCommanderBranding.Instance);
    }
}
