using System;
using LocalAutomation.Avalonia.Bootstrap;

namespace UnrealCommander.Avalonia;

/// <summary>
/// Starts the Unreal Commander launcher while reusing the shared desktop shell.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Starts the desktop lifetime with Unreal Commander shell identity and isolated local state.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        ShellAppBootstrapper.Run(args, UnrealCommanderShellIdentity.Instance);
    }
}
