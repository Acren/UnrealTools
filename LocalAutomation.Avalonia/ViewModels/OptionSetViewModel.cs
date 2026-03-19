using System;
using System.Collections.ObjectModel;
using System.Reflection;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Unreal;

namespace LocalAutomation.Avalonia.ViewModels;

/// <summary>
/// Adapts an operation option set into a generic card with editable fields for the Avalonia parity shell.
/// </summary>
public class OptionSetViewModel : ViewModelBase
{
    /// <summary>
    /// Creates an option set view model around a runtime operation options instance.
    /// </summary>
    public OptionSetViewModel(OperationOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));

        foreach (PropertyInfo property in options.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead)
            {
                continue;
            }

            OptionFieldViewModel field = new(options, property);
            if (field.IsSupported)
            {
                Fields.Add(field);
            }
        }
    }

    /// <summary>
    /// Gets the underlying runtime option set.
    /// </summary>
    public OperationOptions Options { get; }

    /// <summary>
    /// Gets the title shown by the shell.
    /// </summary>
    public string Name => Options.Name;

    /// <summary>
    /// Gets the generic fields supported by the current parity editor.
    /// </summary>
    public ObservableCollection<OptionFieldViewModel> Fields { get; } = new();

    /// <summary>
    /// Indicates whether this option set should render a custom editor instead of the generic field list.
    /// </summary>
    public virtual bool UseCustomEditor => false;

    /// <summary>
    /// Gets the installed engine version choices when a specialized engine-version editor is active.
    /// </summary>
    public virtual ObservableCollection<EngineVersionChoiceViewModel> EngineVersions { get; } = new();

    /// <summary>
    /// Gets the trace channel choices when a specialized Unreal Insights editor is active.
    /// </summary>
    public virtual ObservableCollection<TraceChannelChoiceViewModel> TraceChannels { get; } = new();

    /// <summary>
    /// Indicates whether the custom editor should render a trace channel checklist.
    /// </summary>
    public virtual bool UseTraceChannelEditor => false;

    /// <summary>
    /// Refreshes field values from the underlying option model.
    /// </summary>
    public virtual void Refresh()
    {
        foreach (OptionFieldViewModel field in Fields)
        {
            field.RefreshFromModel();
        }
    }
}
