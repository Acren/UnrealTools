using System;
using System.Collections.Generic;
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

    /// <summary>
    /// The current implementation recreates adapter targets when the UI refreshes, so no in-place refresh is needed.
    /// </summary>
    public void RefreshEditorTarget(object optionSet, object editorTarget)
    {
    }

    /// <summary>
    /// Adapts engine-version selection into a property-grid-friendly checked-list property.
    /// </summary>
    private sealed class EngineVersionOptionEditorTarget : INotifyPropertyChanged
    {
        private readonly List<EngineVersion> _availableVersions;
        private readonly EngineVersionOptions _options;
        private CheckedList<EngineVersion> _enabledVersions;

        /// <summary>
        /// Creates an engine-version editor target for the provided runtime options.
        /// </summary>
        public EngineVersionOptionEditorTarget(EngineVersionOptions options)
        {
            _options = options;
            _availableVersions = EngineFinder.GetLauncherEngineInstallVersions().ToList();
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
            return new CheckedList<EngineVersion>(_availableVersions, _options.EnabledVersions.Value);
        }

        /// <summary>
        /// Copies the checked-list selection into the runtime options object.
        /// </summary>
        private void SyncOptionsFromCheckedList()
        {
            _options.EnabledVersions.Value = _enabledVersions.Items.ToList();
        }
    }
}
