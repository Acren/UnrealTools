using System;
using LocalAutomation.Avalonia.ViewModels;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts a single Unreal Insights trace channel into a checkbox-friendly Avalonia view model.
/// </summary>
public sealed class TraceChannelChoiceViewModel : ViewModelBase
{
    private readonly Action<bool> _setEnabled;
    private bool _enabled;

    /// <summary>
    /// Creates a trace channel choice around the provided trace channel.
    /// </summary>
    public TraceChannelChoiceViewModel(TraceChannel channel, bool enabled, Action<bool> setEnabled)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _enabled = enabled;
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
    }

    /// <summary>
    /// Gets the underlying trace channel.
    /// </summary>
    public TraceChannel Channel { get; }

    /// <summary>
    /// Gets the user-facing trace channel label.
    /// </summary>
    public string DisplayName => Channel.Label;

    /// <summary>
    /// Gets or sets whether the trace channel is enabled.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                _setEnabled(value);
            }
        }
    }

    /// <summary>
    /// Updates the checkbox state from the underlying model without feeding the change back into the model again.
    /// </summary>
    public void RefreshFromModel(bool enabled)
    {
        SetProperty(ref _enabled, enabled);
    }
}
