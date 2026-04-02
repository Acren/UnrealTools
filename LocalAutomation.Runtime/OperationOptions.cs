using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;

#nullable enable

namespace LocalAutomation.Runtime;

/// <summary>
/// Provides the shared runtime model for a configurable option set.
/// </summary>
[TypeConverter(typeof(ExpandableObjectConverter))]
public abstract class OperationOptions : ObservableObject, IComparable<OperationOptions>
{
    private IOperationTarget? _operationTarget;

    /// <summary>
    /// Gets the sort index used to order option groups in the UI.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public virtual int SortIndex => 0;

    /// <summary>
    /// Gets the default user-facing name for the option group.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public virtual string Name
    {
        get
        {
            string name = GetType().Name.Replace("Options", string.Empty);
            return SplitWordsByUppercase(name);
        }
    }

    /// <summary>
    /// Gets or sets the target currently associated with this option set.
    /// </summary>
    [Browsable(false)]
    [JsonIgnore]
    public IOperationTarget? OperationTarget
    {
        get => _operationTarget;
        set
        {
            SetProperty(ref _operationTarget, value);
        }
    }

    /// <summary>
    /// Creates a detached clone of the option set so preview/runtime child parameter copies do not inherit live UI
    /// event subscriptions.
    /// </summary>
    public OperationOptions Clone()
    {
        return ObjectValueSnapshotService.CloneDetached(this);
    }

    /// <summary>
    /// Raises a property-changed notification from within the runtime assembly so derived option sets do not need to
    /// call directly into the toolkit base class across assembly boundaries.
    /// </summary>
    protected void RaiseOptionsChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
    }

    /// <summary>
    /// Re-exposes toolkit property-changing notifications from within this assembly so generated option setters in
    /// downstream assemblies do not call directly into ObservableObject across assembly boundaries.
    /// </summary>
    protected new void OnPropertyChanging(PropertyChangingEventArgs e)
    {
        base.OnPropertyChanging(e);
    }

    /// <summary>
    /// Re-exposes toolkit property-changing notifications from within this assembly so generated option setters in
    /// downstream assemblies can use the string-based overload safely.
    /// </summary>
    protected new void OnPropertyChanging([CallerMemberName] string? propertyName = null)
    {
        base.OnPropertyChanging(propertyName);
    }

    /// <summary>
    /// Re-exposes toolkit property-changed notifications from within this assembly so generated option setters in
    /// downstream assemblies do not call directly into ObservableObject across assembly boundaries.
    /// </summary>
    protected new void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
    }

    /// <summary>
    /// Re-exposes toolkit property-changed notifications from within this assembly so generated option setters in
    /// downstream assemblies can use the string-based overload safely.
    /// </summary>
    protected new void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
    }

    /// <summary>
    /// Orders option sets first by sort index and then by display name.
    /// </summary>
    public int CompareTo(OperationOptions? other)
    {
        if (other == null)
        {
            return 1;
        }

        if (SortIndex != other.SortIndex)
        {
            return SortIndex.CompareTo(other.SortIndex);
        }

        return string.Compare(Name, other.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Expands PascalCase identifiers into space-separated words for UI labels.
    /// </summary>
    private static string SplitWordsByUppercase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        System.Text.StringBuilder builder = new();
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (index > 0 && char.IsUpper(current))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }
}
