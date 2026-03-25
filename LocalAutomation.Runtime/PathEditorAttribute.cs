using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Describes a neutral path-editing intent that UI layers can translate into a concrete file or directory picker.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PathEditorAttribute : Attribute
{
    /// <summary>
    /// Creates path-editor metadata for the provided picker kind.
    /// </summary>
    public PathEditorAttribute(PathEditorKind kind)
    {
        Kind = kind;
    }

    /// <summary>
    /// Gets the picker kind the UI should expose.
    /// </summary>
    public PathEditorKind Kind { get; }

    /// <summary>
    /// Gets or sets the picker title presented by UI shells.
    /// </summary>
    public string? Title { get; set; }
}

/// <summary>
/// Identifies the path-picker shape a UI should present for one string property.
/// </summary>
public enum PathEditorKind
{
    /// <summary>
    /// Presents a file picker.
    /// </summary>
    File,

    /// <summary>
    /// Presents a directory picker.
    /// </summary>
    Directory
}
