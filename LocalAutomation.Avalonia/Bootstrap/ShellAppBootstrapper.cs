using System;
using System.IO;
using Avalonia;
using LocalAutomation.Application;
using LocalAutomation.Core;
using LocalAutomation.Extensions.Abstractions;
using Microsoft.Extensions.Logging;

namespace LocalAutomation.Avalonia.Bootstrap;

/// <summary>
/// Starts the shared shell around a launcher-provided application host so composition stays outside the UI
/// assembly.
/// </summary>
public static class ShellAppBootstrapper
{
    /// <summary>
     /// Starts the desktop lifetime using bundled extension discovery.
     /// </summary>
    public static void Run(string[] args)
    {
        Run(args, ShellIdentity.LocalAutomation);
    }

    /// <summary>
    /// Starts the desktop lifetime using the provided launcher branding and bundled extension discovery.
    /// </summary>
    public static void Run(string[] args, ShellIdentity shellIdentity)
    {
        App.ConfigureShellIdentity(shellIdentity);
        ApplicationLogService.Initialize();

        try
        {
            ExtensionLoadResult extensionLoadResult = BundledExtensionLoader.LoadBundledExtensions();
            LocalAutomationApplicationHost services = CreateApplicationHost(extensionLoadResult);
            App.ConfigureServices(services);
            LogExtensionDiscovery(extensionLoadResult);
            BuildApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            ApplicationLogService.LogStartupException(ex);
            throw;
        }
    }

    /// <summary>
    /// Flushes the file-backed logging pipeline when the current process exits normally.
    /// </summary>
    static ShellAppBootstrapper()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ApplicationLogService.Shutdown();
    }

    /// <summary>
    /// Configures the shared desktop application builder used by launcher entry points.
    /// </summary>
    public static AppBuilder BuildApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }

    /// <summary>
    /// Creates an application host from all successfully discovered modules.
    /// </summary>
    private static LocalAutomationApplicationHost CreateApplicationHost(ExtensionLoadResult extensionLoadResult)
    {
        ExtensionCatalog catalog = new();
        foreach (IExtensionModule module in extensionLoadResult.Modules)
        {
            try
            {
                catalog.RegisterModule(module);
            }
            catch (Exception ex)
            {
                extensionLoadResult.Errors.Add($"Failed to register extension module '{module.Id}': {ex.Message}");
            }
        }

        string appDataRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            App.ShellIdentity.DataFolderName);
        return new LocalAutomationApplicationHost(catalog, appDataRootPath, App.ShellIdentity.TargetSettingsFileName);
    }

    /// <summary>
    /// Emits a concise discovery summary plus detailed warnings and errors into the startup log.
    /// </summary>
    private static void LogExtensionDiscovery(ExtensionLoadResult extensionLoadResult)
    {
        ApplicationLogService.LogInformation($"Discovered {extensionLoadResult.Modules.Count} extension module(s) during startup.");

        foreach (string warning in extensionLoadResult.Warnings)
        {
            ApplicationLogger.Logger.LogWarning("Extension discovery warning: {Warning}", warning);
        }

        foreach (string error in extensionLoadResult.Errors)
        {
            ApplicationLogger.Logger.LogError("Extension discovery error: {Error}", error);
        }
    }
}
