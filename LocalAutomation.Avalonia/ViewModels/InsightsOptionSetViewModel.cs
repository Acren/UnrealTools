using System;
using System.Collections.ObjectModel;
using System.Linq;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Provides the specialized checkbox-list editor used for Unreal Insights trace channel selection.
/// </summary>
public sealed class InsightsOptionSetViewModel : OptionSetViewModel
{
    private readonly InsightsOptions _options;

    /// <summary>
    /// Creates an Insights-specific option editor around the provided runtime options instance.
    /// </summary>
    public InsightsOptionSetViewModel(InsightsOptions options)
        : base(options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        foreach (TraceChannel channel in UnrealAutomationCommon.Unreal.TraceChannels.Channels)
        {
            bool enabled = _options.TraceChannels.Contains(channel);
            TraceChannels.Add(new TraceChannelChoiceViewModel(channel, enabled, isEnabled => SetChannelEnabled(channel, isEnabled)));
        }
    }

    /// <summary>
    /// Enables the custom trace-channel editor in place of the generic field list.
    /// </summary>
    public override bool UseCustomEditor => true;

    /// <summary>
    /// Indicates that this custom editor should render the trace channel checklist.
    /// </summary>
    public override bool UseTraceChannelEditor => true;

    /// <summary>
    /// Gets the trace channel checklist rendered by the Insights editor.
    /// </summary>
    public override ObservableCollection<TraceChannelChoiceViewModel> TraceChannels { get; } = new();

    /// <summary>
    /// Refreshes checkbox values from the underlying model in case restore or coercion changed the selection.
    /// </summary>
    public override void Refresh()
    {
        base.Refresh();

        foreach (TraceChannelChoiceViewModel channel in TraceChannels)
        {
            bool isEnabled = _options.TraceChannels.Contains(channel.Channel);
            channel.RefreshFromModel(isEnabled);
        }
    }

    /// <summary>
    /// Adds or removes a trace channel from the underlying options model.
    /// </summary>
    private void SetChannelEnabled(TraceChannel channel, bool isEnabled)
    {
        bool currentlyEnabled = _options.TraceChannels.Contains(channel);
        if (isEnabled == currentlyEnabled)
        {
            return;
        }

        if (isEnabled)
        {
            _options.TraceChannels.Add(channel);
            return;
        }

        TraceChannel? existingChannel = _options.TraceChannels.FirstOrDefault(item => item.Equals(channel));
        if (existingChannel != null)
        {
            _options.TraceChannels.Remove(existingChannel);
        }
    }
}
