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
    private bool _enableOperationSwitchPerformanceTelemetry;

    /// <summary>
    /// Raised whenever one persisted application preference changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets whether operation-switch timing summaries should be written to the shared shell log output.
    /// </summary>
    [DisplayName("Enable performance telemetry")]
    [Description("Writes readable operation-switch timing diagnostics to the application log for troubleshooting slow UI transitions.")]
    [PersistedValue(PersistenceScope.Global)]
    public bool EnableOperationSwitchPerformanceTelemetry
    {
        get => _enableOperationSwitchPerformanceTelemetry;
        set
        {
            if (_enableOperationSwitchPerformanceTelemetry == value)
            {
                return;
            }

            _enableOperationSwitchPerformanceTelemetry = value;
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
