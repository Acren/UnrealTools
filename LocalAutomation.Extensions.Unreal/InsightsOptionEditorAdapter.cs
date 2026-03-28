using System;
using System.ComponentModel;
using LocalAutomation.Extensions.Abstractions;
using PropertyModels.Collections;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Projects <see cref="InsightsOptions"/> into a checked-list shape the property grid can render natively.
/// </summary>
public sealed class InsightsOptionEditorAdapter : IOptionEditorAdapter
{
    /// <summary>
    /// Gets the stable adapter identifier.
    /// </summary>
    public string Id => "unreal.option-editor.insights";

    /// <summary>
    /// Returns whether the provided option set is an insights options instance.
    /// </summary>
    public bool CanAdapt(object optionSet)
    {
        return optionSet is InsightsOptions;
    }

    /// <summary>
    /// Creates the property-grid adapter target for trace channel selection.
    /// </summary>
    public object CreateEditorTarget(object optionSet)
    {
        return new InsightsOptionEditorTarget((InsightsOptions)optionSet);
    }

    /// <summary>
    /// The current implementation recreates adapter targets when the UI refreshes, so no in-place refresh is needed.
    /// </summary>
    public void RefreshEditorTarget(object optionSet, object editorTarget)
    {
    }

    /// <summary>
    /// Adapts insights trace-channel selection into a property-grid-friendly checked-list property.
    /// </summary>
    private sealed class InsightsOptionEditorTarget : INotifyPropertyChanged
    {
        private readonly InsightsOptions _options;
        private CheckedList<TraceChannel> _traceChannels;

        /// <summary>
        /// Creates a trace-channel editor target for the provided runtime options.
        /// </summary>
        public InsightsOptionEditorTarget(InsightsOptions options)
        {
            _options = options;
            _traceChannels = CreateCheckedList();
            _traceChannels.SelectionChanged += HandleSelectionChanged;
        }

        /// <summary>
        /// Gets or sets the trace channels as a checked-list.
        /// </summary>
        [DisplayName("Trace Channels")]
        [Description("Selects the Unreal Insights trace channels to enable for the launched process.")]
        public CheckedList<TraceChannel> TraceChannels
        {
            get => _traceChannels;
            set
            {
                if (ReferenceEquals(_traceChannels, value) || value == null)
                {
                    return;
                }

                _traceChannels.SelectionChanged -= HandleSelectionChanged;
                _traceChannels = value;
                _traceChannels.SelectionChanged += HandleSelectionChanged;
                SyncOptionsFromCheckedList();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TraceChannels)));
            }
        }

        /// <summary>
        /// Raised when the checked-list property changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Syncs checklist selection changes back into the runtime options object.
        /// </summary>
        private void HandleSelectionChanged(object? sender, EventArgs e)
        {
            SyncOptionsFromCheckedList();
        }

        /// <summary>
        /// Creates a checked-list seeded from the known trace channels and the currently selected subset.
        /// </summary>
        private CheckedList<TraceChannel> CreateCheckedList()
        {
            return new CheckedList<TraceChannel>(UnrealAutomationCommon.Unreal.TraceChannels.Channels, _options.TraceChannels);
        }

        /// <summary>
        /// Copies the checked-list selection into the runtime options object.
        /// </summary>
        private void SyncOptionsFromCheckedList()
        {
            _options.TraceChannels.Clear();
            foreach (TraceChannel channel in _traceChannels.Items)
            {
                _options.TraceChannels.Add(channel);
            }
        }
    }
}
