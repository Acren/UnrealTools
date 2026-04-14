using System;
using System.ComponentModel;
using LocalAutomation.Application;
using LocalAutomation.Avalonia.Diagnostics;
using LocalAutomation.Core;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Drives the global application settings window and saves host-wide preferences through the shared debounced
/// background persistence mechanism.
/// </summary>
public sealed class SettingsWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly LocalAutomationApplicationHost _services;
    private readonly DebouncedBackgroundSaver<PersistedSettingsWriteBatch> _settingsSaver;
    private bool _disposed;

    /// <summary>
    /// Creates the settings window view model around the shared application host.
    /// </summary>
    public SettingsWindowViewModel(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _settingsSaver = new DebouncedBackgroundSaver<PersistedSettingsWriteBatch>(
            debounceDelay: SaveDebounceDelay,
            saveState: _services.OptionValues.SaveCapturedSettings,
            mergeStates: static (earlier, later) => earlier.Merge(later),
            handleSaveException: HandleSaveException);
        _services.ApplicationSettings.PropertyChanged += HandleApplicationSettingsChanged;
    }

    /// <summary>
    /// Gets the shared application settings object rendered directly by the property grid.
    /// </summary>
    public object Settings => _services.ApplicationSettings;

    /// <summary>
    /// Saves any pending settings changes immediately.
    /// </summary>
    public void FlushPendingSave()
    {
        // Capture one last detached batch before closing so the background saver persists the latest in-memory state.
        _settingsSaver.Flush(_services.OptionValues.CaptureGlobalSettings(_services.ApplicationSettings));
    }

    /// <summary>
    /// Unsubscribes from the shared settings instance when the window closes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _services.ApplicationSettings.PropertyChanged -= HandleApplicationSettingsChanged;
        _settingsSaver.Dispose();
    }

    /// <summary>
    /// Applies any immediate runtime side effects and queues a debounced save whenever the user edits a global setting.
    /// </summary>
    private void HandleApplicationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Apply runtime-facing settings immediately so the shell reflects telemetry and output-path updates without
        // requiring a restart.
        _services.ApplyApplicationSettings();

        PerformanceTelemetryListener.Start(
            _services.ApplicationSettings.EnablePerformanceTelemetry,
            TimeSpan.FromMilliseconds(_services.ApplicationSettings.MinimumPerformanceTelemetryMilliseconds),
            TimeSpan.FromMilliseconds(_services.ApplicationSettings.MinimumCollapsedPerformanceTelemetryScopeMilliseconds));
        // Capture a detached persisted-value batch immediately so the background saver never touches the live settings
        // object after the UI continues processing.
        _settingsSaver.RequestSave(_services.OptionValues.CaptureGlobalSettings(_services.ApplicationSettings));
    }

    /// <summary>
    /// Logs background save failures into the shared application log so settings persistence issues remain visible.
    /// </summary>
    private static void HandleSaveException(Exception exception)
    {
        ApplicationLogService.LogError(exception, "Failed to save global application settings.");
    }
}
