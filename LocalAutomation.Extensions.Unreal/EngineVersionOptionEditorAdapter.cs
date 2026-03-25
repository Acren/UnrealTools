using System;
using System.ComponentModel;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using PropertyModels.Collections;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Projects <see cref="EngineVersionOptions"/> into a checked-list shape the property grid can render natively.
/// </summary>
public sealed class EngineVersionOptionEditorAdapter : IOptionEditorAdapter
{
    /// <summary>
    /// Gets the stable adapter identifier.
    /// </summary>
    public string Id => "unreal.option-editor.engine-version";

    /// <summary>
    /// Returns whether the provided option set is an engine-version options instance.
    /// </summary>
    public bool CanAdapt(object optionSet)
    {
        return optionSet is EngineVersionOptions;
    }

    /// <summary>
    /// Creates the property-grid adapter target for engine version selection.
    /// </summary>
    public object CreateEditorTarget(object optionSet)
    {
        return new EngineVersionOptionEditorTarget((EngineVersionOptions)optionSet);
    }

    public void RefreshEditorTarget(object optionSet, object editorTarget)
    {
        if (optionSet is not EngineVersionOptions engineVersionOptions || editorTarget is not EngineVersionOptionEditorTarget target)
        {
            return;
        }

        target.Refresh(engineVersionOptions);
    }

    /// <summary>
    /// Adapts engine-version selection into a property-grid-friendly checked-list property.
    /// </summary>
    private sealed class EngineVersionOptionEditorTarget : INotifyPropertyChanged
    {
        private readonly EngineVersionOptions _options;
        private CheckedList<EngineVersion> _enabledVersions;

        /// <summary>
        /// Creates an engine-version editor target for the provided runtime options.
        /// </summary>
        public EngineVersionOptionEditorTarget(EngineVersionOptions options)
        {
            _options = options;
            _enabledVersions = CreateCheckedList();
            _enabledVersions.SelectionChanged += HandleSelectionChanged;
        }

        /// <summary>
        /// Gets or sets the installed engine versions as a checked-list.
        /// </summary>
        [DisplayName("Enabled Versions")]
        public CheckedList<EngineVersion> EnabledVersions
        {
            get => _enabledVersions;
            set
            {
                if (ReferenceEquals(_enabledVersions, value) || value == null)
                {
                    return;
                }

                _enabledVersions.SelectionChanged -= HandleSelectionChanged;
                _enabledVersions = value;
                _enabledVersions.SelectionChanged += HandleSelectionChanged;
                SyncOptionsFromCheckedList();
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnabledVersions)));
            }
        }

        /// <summary>
        /// Raised when the checked-list property changes.
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Rebuilds the checklist from the current runtime option state so cached editor targets stay aligned with the
        /// underlying options object after operation or target changes.
        /// </summary>
        public void Refresh(EngineVersionOptions options)
        {
            if (!ReferenceEquals(_options, options))
            {
                return;
            }

            CheckedList<EngineVersion> refreshedList = CreateCheckedList();
            if (ReferenceEquals(_enabledVersions, refreshedList))
            {
                return;
            }

            _enabledVersions.SelectionChanged -= HandleSelectionChanged;
            _enabledVersions = refreshedList;
            _enabledVersions.SelectionChanged += HandleSelectionChanged;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnabledVersions)));
        }

        /// <summary>
        /// Syncs checklist selection changes back into the runtime options object.
        /// </summary>
        private void HandleSelectionChanged(object? sender, EventArgs e)
        {
            SyncOptionsFromCheckedList();
        }

        /// <summary>
        /// Creates a checked-list seeded from the installed engine versions and the currently enabled selection.
        /// </summary>
        private CheckedList<EngineVersion> CreateCheckedList()
        {
            return new CheckedList<EngineVersion>(EngineFinder.GetLauncherEngineInstallVersions().ToList(), _options.EnabledVersions);
        }

        /// <summary>
         /// Copies the checked-list selection into the runtime options object.
         /// </summary>
        private void SyncOptionsFromCheckedList()
        {
            _options.EnabledVersions.Clear();
            foreach (EngineVersion version in _enabledVersions.Items)
            {
                _options.EnabledVersions.Add(version);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnabledVersions)));
        }
    }
}
