using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalAutomation.Runtime;

namespace LocalAutomation.Application;

/// <summary>
/// Stores durable application-wide preferences for one launcher host.
/// </summary>
[PersistedSettings("app")]
public sealed class ApplicationSettings : INotifyPropertyChanged
{
    private bool _enablePerformanceTelemetry;
    private bool _revealHiddenTasks;
    private double _minimumPerformanceTelemetryMilliseconds;
    private double _minimumCollapsedPerformanceTelemetryScopeMilliseconds;
    private readonly string _defaultOutputRootPath;
    private readonly string _defaultTempRootPath;
    private string _outputRootPath;
    private string _tempRootPath;

    /// <summary>
    /// Creates one application-settings instance with host-specific path defaults.
    /// </summary>
    public ApplicationSettings(string? defaultOutputRootPath = null, string? defaultTempRootPath = null)
    {
        _defaultOutputRootPath = string.IsNullOrWhiteSpace(defaultOutputRootPath) ? OutputPaths.DefaultRootPath : defaultOutputRootPath.Trim();
        _defaultTempRootPath = string.IsNullOrWhiteSpace(defaultTempRootPath) ? OutputPaths.GetDefaultTempRootPath() : defaultTempRootPath.Trim();
        _outputRootPath = _defaultOutputRootPath;
        _tempRootPath = _defaultTempRootPath;
    }

    /// <summary>
    /// Raised whenever one persisted application preference changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets the shared root directory under which automation output is written.
    /// </summary>
    [DisplayName("Output root path")]
    [Description("Controls the base folder used for generated automation output, archives, reports, and temporary run artifacts.")]
    [PathEditor(PathEditorKind.Directory, Title = "Select output root folder")]
    [PersistedValue(PersistenceScope.Global)]
    public string OutputRootPath
    {
        get => _outputRootPath;
        set
        {
            string normalizedValue = string.IsNullOrWhiteSpace(value)
                ? _defaultOutputRootPath
                : value.Trim();

            if (string.Equals(_outputRootPath, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            _outputRootPath = normalizedValue;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the shared root directory under which temporary automation workspaces are written.
    /// </summary>
    [DisplayName("Temp root path")]
    [Description("Controls the base folder used for temporary run workspaces and scratch files. Defaults to a folder under the system TEMP directory.")]
    [PathEditor(PathEditorKind.Directory, Title = "Select temp root folder")]
    [PersistedValue(PersistenceScope.Global)]
    public string TempRootPath
    {
        get => _tempRootPath;
        set
        {
            string normalizedValue = string.IsNullOrWhiteSpace(value)
                ? _defaultTempRootPath
                : value.Trim();

            if (string.Equals(_tempRootPath, normalizedValue, StringComparison.Ordinal))
            {
                return;
            }

            _tempRootPath = normalizedValue;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether performance telemetry should be written to the shared shell log output.
    /// </summary>
    [DisplayName("Enable performance telemetry")]
    [Description("Writes readable application-wide timing diagnostics to the application log for troubleshooting instrumented workflows across the shell and automation stack.")]
    [PersistedValue(PersistenceScope.Global)]
    public bool EnablePerformanceTelemetry
    {
        get => _enablePerformanceTelemetry;
        set
        {
            if (_enablePerformanceTelemetry == value)
            {
                return;
            }

            _enablePerformanceTelemetry = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the minimum duration in milliseconds required before a completed root telemetry activity is logged.
    /// </summary>
    [DisplayName("Minimum root telemetry time (ms)")]
    [Description("Suppresses completed root performance telemetry traces shorter than this duration so the log only includes slower top-level workflows. Set to 0 to log every captured root trace.")]
    [PersistedValue(PersistenceScope.Global)]
    public double MinimumPerformanceTelemetryMilliseconds
    {
        get => _minimumPerformanceTelemetryMilliseconds;
        set
        {
            double normalizedValue = double.IsNaN(value) || double.IsInfinity(value)
                ? 0
                : Math.Max(0, value);
            if (Math.Abs(_minimumPerformanceTelemetryMilliseconds - normalizedValue) < 0.001)
            {
                return;
            }

            _minimumPerformanceTelemetryMilliseconds = normalizedValue;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the minimum duration in milliseconds required before a nested telemetry scope remains visible in
    /// the logged timing tree.
    /// </summary>
    [DisplayName("Minimum collapsed telemetry scope time (ms)")]
    [Description("Collapses nested performance telemetry scopes shorter than this duration so the printed timing tree stays focused on meaningful work. Set to 0 to keep every nested scope visible.")]
    [PersistedValue(PersistenceScope.Global)]
    public double MinimumCollapsedPerformanceTelemetryScopeMilliseconds
    {
        get => _minimumCollapsedPerformanceTelemetryScopeMilliseconds;
        set
        {
            double normalizedValue = double.IsNaN(value) || double.IsInfinity(value)
                ? 0
                : Math.Max(0, value);
            if (Math.Abs(_minimumCollapsedPerformanceTelemetryScopeMilliseconds - normalizedValue) < 0.001)
            {
                return;
            }

            _minimumCollapsedPerformanceTelemetryScopeMilliseconds = normalizedValue;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether graph views should reveal tasks that are normally collapsed as internal implementation
    /// detail.
    /// </summary>
    [DisplayName("Reveal hidden tasks")]
    [Description("Shows graph tasks that are normally collapsed, including internal body nodes and any authored tasks marked as hidden.")]
    [PersistedValue(PersistenceScope.Global)]
    public bool RevealHiddenTasks
    {
        get => _revealHiddenTasks;
        set
        {
            if (_revealHiddenTasks == value)
            {
                return;
            }

            _revealHiddenTasks = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Raises the property-changed event for the provided preference name.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
