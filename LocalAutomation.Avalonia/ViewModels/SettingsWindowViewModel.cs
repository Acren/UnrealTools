using System;
using System.ComponentModel;
using Avalonia.Threading;
using LocalAutomation.Application;
using LocalAutomation.Avalonia.Diagnostics;
using LocalAutomationApplicationHost = LocalAutomation.Application.LocalAutomationApplicationHost;
using PropertyModels.ComponentModel.DataAnnotations;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Drives the global application settings window and saves host-wide preferences with a small debounce.
/// </summary>
public sealed class SettingsWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly LocalAutomationApplicationHost _services;
    private readonly DispatcherTimer _saveTimer;
    private readonly SettingsPropertyGridTarget _propertyGridTarget;
    private bool _disposed;
    private bool _hasPendingSave;

    /// <summary>
    /// Creates the settings window view model around the shared application host.
    /// </summary>
    public SettingsWindowViewModel(LocalAutomationApplicationHost services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _propertyGridTarget = new SettingsPropertyGridTarget(_services.ApplicationSettings);
        _saveTimer = new DispatcherTimer { Interval = SaveDebounceDelay };
        _saveTimer.Tick += HandleSaveTimerTick;
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
    /// Applies any immediate runtime side effects and queues a debounced save whenever the user edits a global setting.
    /// </summary>
    private void HandleApplicationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Apply runtime-facing settings immediately so the shell reflects telemetry and output-path updates without
        // requiring a restart.
        _services.ApplyApplicationSettings();

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
