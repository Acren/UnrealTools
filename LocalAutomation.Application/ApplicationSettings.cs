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
    private bool _autoCopyPrimaryCommandAfterExecute;
    private bool _retainCompletedTaskTabs = true;
    private bool _showStartupDiscoveryWarnings = true;

    /// <summary>
    /// Raised whenever one persisted application preference changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets whether executing an operation also copies the primary command line to the clipboard.
    /// </summary>
    [DisplayName("Auto-copy command after execute")]
    [Description("Copies the first command preview line to the clipboard when an operation starts from the command panel.")]
    [PersistedValue(PersistenceScope.Global)]
    public bool AutoCopyPrimaryCommandAfterExecute
    {
        get => _autoCopyPrimaryCommandAfterExecute;
        set
        {
            if (_autoCopyPrimaryCommandAfterExecute == value)
            {
                return;
            }

            _autoCopyPrimaryCommandAfterExecute = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether completed runtime tabs remain visible until the user closes them.
    /// </summary>
    [DisplayName("Retain completed task tabs")]
    [Description("Keeps finished runtime task tabs open so their logs remain visible after execution completes.")]
    [PersistedValue(PersistenceScope.Global)]
    public bool RetainCompletedTaskTabs
    {
        get => _retainCompletedTaskTabs;
        set
        {
            if (_retainCompletedTaskTabs == value)
            {
                return;
            }

            _retainCompletedTaskTabs = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether startup discovery warnings stay visible in the shell header.
    /// </summary>
    [DisplayName("Show startup discovery warnings")]
    [Description("Shows the startup warning banner when bundled extensions are missing or fail to load.")]
    [PersistedValue(PersistenceScope.Global)]
    public bool ShowStartupDiscoveryWarnings
    {
        get => _showStartupDiscoveryWarnings;
        set
        {
            if (_showStartupDiscoveryWarnings == value)
            {
                return;
            }

            _showStartupDiscoveryWarnings = value;
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
