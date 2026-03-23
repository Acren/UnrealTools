namespace LocalAutomation.Application;

/// <summary>
/// Provides shared host-storage defaults for application services that run outside the Avalonia shell assembly.
/// </summary>
public static class LocalAutomationHostStorage
{
    /// <summary>
    /// Gets the default LocalAppData folder name used when a host does not supply a branded override.
    /// </summary>
    public const string DefaultDataFolderName = "LocalAutomation";

    /// <summary>
    /// Gets the default repo-local layered settings filename used when a host does not supply a branded override.
    /// </summary>
    public const string DefaultTargetSettingsFileName = ".localautomation.json";
}
