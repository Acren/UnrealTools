using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;

[assembly: AvaloniaTestApplication(typeof(LocalAutomation.Avalonia.Tests.AvaloniaHeadlessTestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace LocalAutomation.Avalonia.Tests;

/// <summary>
/// Reuses the production Avalonia application resources under the headless platform so view-model tests see the same
/// dispatcher, styles, and resource initialization shape as the desktop shell.
/// </summary>
public static class AvaloniaHeadlessTestAppBuilder
{
    /// <summary>
    /// Builds the headless Avalonia application used by the xUnit integration tests.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<global::LocalAutomation.Avalonia.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
