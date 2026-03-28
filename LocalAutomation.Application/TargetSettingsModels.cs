using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalAutomation.Runtime;

namespace LocalAutomation.Application;

/// <summary>
/// Stores target-scoped persisted configuration shared by multiple operations for a selected target.
/// </summary>
[PersistedSettings("target")]
public sealed class TargetSettings
{
    private string _testFilter = string.Empty;

    /// <summary>
    /// Raised whenever one target-scoped setting changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets or sets the default automation test filter associated with the current target.
    /// </summary>
    [PersistedValue(PersistenceScope.TargetLocal)]
    public string TestFilter
    {
        get => _testFilter;
        set
        {
            _testFilter = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Raises the property-changed event for the provided property name.
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
