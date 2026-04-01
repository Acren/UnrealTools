using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalAutomation.Runtime;

namespace LocalAutomation.Application;

/// <summary>
/// Stores durable application-wide preferences for one branded launcher host.
/// </summary>
[PersistedSettings("app")]
public sealed class ApplicationSettings : INotifyPropertyChanged
{
    private bool _enablePerformanceTelemetry;
    private double _minimumPerformanceTelemetryMilliseconds;
    private string _outputRootPath = OutputPaths.DefaultRootPath;

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
                ? OutputPaths.DefaultRootPath
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
    [DisplayName("Minimum telemetry time (ms)")]
    [Description("Suppresses completed performance telemetry roots shorter than this duration so the log focuses on meaningful delays. Set to 0 to log every captured trace.")]
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
    /// Raises the property-changed event for the provided preference name.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
