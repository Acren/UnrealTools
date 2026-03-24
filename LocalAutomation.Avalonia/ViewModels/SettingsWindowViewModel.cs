using System;
using System.ComponentModel;
using LocalAutomation.Application;
using LocalAutomation.Avalonia.Diagnostics;
using LocalAutomation.Core;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;
using PropertyModels.ComponentModel.DataAnnotations;

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
    private readonly SettingsPropertyGridTarget _propertyGridTarget;
    private bool _disposed;

    /// <summary>
    /// Creates the settings window view model around the shared application host.
    /// </summary>
    public SettingsWindowViewModel(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _propertyGridTarget = new SettingsPropertyGridTarget(_services.ApplicationSettings);
        _settingsSaver = new DebouncedBackgroundSaver<PersistedSettingsWriteBatch>(
            debounceDelay: SaveDebounceDelay,
            saveState: _services.OptionValues.SaveCapturedSettings,
            mergeStates: static (earlier, later) => earlier.Merge(later),
            handleSaveException: HandleSaveException);
        _services.ApplicationSettings.PropertyChanged += HandleApplicationSettingsChanged;
    }

    /// <summary>
    /// Gets the settings projection rendered by the property grid.
    /// </summary>
    public object PropertyGridTarget => _propertyGridTarget;

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

        PerformanceTelemetryListener.Start(_services.ApplicationSettings.EnablePerformanceTelemetry);
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

    /// <summary>
    /// Exposes application settings to the property grid with Avalonia-specific editor metadata.
    /// </summary>
    private sealed class SettingsPropertyGridTarget
    {
        private readonly ApplicationSettings _settings;

        /// <summary>
        /// Creates a projection over the shared application settings object.
        /// </summary>
        public SettingsPropertyGridTarget(ApplicationSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// Gets or sets the shared root directory under which automation output is written.
        /// </summary>
        [DisplayName("Output root path")]
        [Description("Controls the base folder used for generated automation output, archives, reports, and temporary run artifacts.")]
        [PathBrowsable(PathBrowsableType.Directory, false, Title = "Select output root folder")]
        public string OutputRootPath
        {
            get => _settings.OutputRootPath;
            set => _settings.OutputRootPath = value;
        }

        /// <summary>
        /// Gets or sets whether performance telemetry should be written to the shared shell log output.
        /// </summary>
        [DisplayName("Enable performance telemetry")]
        [Description("Writes readable application-wide timing diagnostics to the application log for troubleshooting instrumented workflows across the shell and automation stack.")]
        public bool EnablePerformanceTelemetry
        {
            get => _settings.EnablePerformanceTelemetry;
            set => _settings.EnablePerformanceTelemetry = value;
        }
    }
}
