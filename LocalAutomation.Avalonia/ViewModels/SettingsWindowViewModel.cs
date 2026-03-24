using System;
using System.ComponentModel;
using Avalonia.Threading;
using LocalAutomation.Application;
using LocalAutomation.Avalonia.Diagnostics;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Drives the global application settings window and saves host-wide preferences with a small debounce.
/// </summary>
public sealed class SettingsWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly LocalAutomationApplicationHost _services;
    private readonly DispatcherTimer _saveTimer;
    private bool _disposed;
    private bool _hasPendingSave;

    /// <summary>
    /// Creates the settings window view model around the shared application host.
    /// </summary>
    public SettingsWindowViewModel(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _saveTimer = new DispatcherTimer { Interval = SaveDebounceDelay };
        _saveTimer.Tick += HandleSaveTimerTick;
        _services.ApplicationSettings.PropertyChanged += HandleApplicationSettingsChanged;
    }

    /// <summary>
    /// Gets the shared settings object rendered by the property grid.
    /// </summary>
    public ApplicationSettings PropertyGridTarget => _services.ApplicationSettings;

    /// <summary>
    /// Saves any pending settings changes immediately.
    /// </summary>
    public void FlushPendingSave()
    {
        if (!_hasPendingSave)
        {
            return;
        }

        _saveTimer.Stop();
        SaveSettings();
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
        _saveTimer.Stop();
        _saveTimer.Tick -= HandleSaveTimerTick;
        _services.ApplicationSettings.PropertyChanged -= HandleApplicationSettingsChanged;
    }

    /// <summary>
    /// Queues a debounced save whenever the user edits a global setting.
    /// </summary>
    private void HandleApplicationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Apply the listener toggle immediately so telemetry can start or stop without requiring an app restart.
        PerformanceTelemetryListener.Start(_services.ApplicationSettings.EnablePerformanceTelemetry);
        _hasPendingSave = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>
    /// Commits the latest pending global settings changes to disk.
    /// </summary>
    private void HandleSaveTimerTick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        SaveSettings();
    }

    /// <summary>
    /// Writes the shared application settings object into the host-global settings file.
    /// </summary>
    private void SaveSettings()
    {
        _hasPendingSave = false;
        _services.OptionValues.SaveGlobalSettings(_services.ApplicationSettings);
    }
}
