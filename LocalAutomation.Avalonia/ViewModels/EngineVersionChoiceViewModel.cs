using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Represents a selectable installed engine version in the Avalonia parity shell.
/// </summary>
public sealed class EngineVersionChoiceViewModel : INotifyPropertyChanged
{
    private bool _enabled;

    /// <summary>
    /// Creates an engine version choice for the provided installed version.
    /// </summary>
    public EngineVersionChoiceViewModel(EngineVersion engineVersion)
    {
        EngineVersion = engineVersion;
    }

    /// <summary>
    /// Gets the installed engine version represented by this choice.
    /// </summary>
    public EngineVersion EngineVersion { get; }

    /// <summary>
    /// Gets the user-facing label for the installed engine version.
    /// </summary>
    public string DisplayName => EngineVersion.MajorMinorString;

    /// <summary>
    /// Gets or sets whether this engine version is currently enabled.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            }
        }
    }

    /// <summary>
    /// Raised whenever the enabled state changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}
