using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Provides the checkbox-based installed-engine editor for <see cref="EngineVersionOptions"/>.
/// </summary>
public sealed class EngineVersionOptionSetViewModel : OptionSetViewModel
{
    /// <summary>
    /// Creates the installed-engine selector around the provided options instance.
    /// </summary>
    public EngineVersionOptionSetViewModel(EngineVersionOptions options)
        : base(options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));

        foreach (EngineVersion version in EngineFinder.GetLauncherEngineInstallVersions())
        {
            EngineVersionChoiceViewModel choice = new(version)
            {
                Enabled = Options.EnabledVersions.Value.Contains(version)
            };

            choice.PropertyChanged += HandleChoicePropertyChanged;
            EngineVersions.Add(choice);
        }
    }

    /// <summary>
    /// Gets the specialized engine version options instance.
    /// </summary>
    public new EngineVersionOptions Options { get; }

    /// <summary>
    /// Gets the installed engine versions surfaced as checkbox choices.
    /// </summary>
    public override ObservableCollection<EngineVersionChoiceViewModel> EngineVersions { get; } = new();

    /// <summary>
    /// Indicates that this option set should render the installed-engine selector instead of the generic field list.
    /// </summary>
    public override bool UseCustomEditor => true;

    /// <summary>
    /// Refreshes the checkbox state from the underlying options object.
    /// </summary>
    public override void Refresh()
    {
        List<EngineVersion> enabledVersions = Options.EnabledVersions.Value;
        foreach (EngineVersionChoiceViewModel choice in EngineVersions)
        {
            choice.Enabled = enabledVersions.Contains(choice.EngineVersion);
        }

        base.Refresh();
    }

    /// <summary>
    /// Syncs the enabled checkbox selections back into the live options object.
    /// </summary>
    private void HandleChoicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EngineVersionChoiceViewModel.Enabled))
        {
            return;
        }

        Options.EnabledVersions.Value = EngineVersions
            .Where(choice => choice.Enabled)
            .Select(choice => choice.EngineVersion)
            .ToList();
    }
}
