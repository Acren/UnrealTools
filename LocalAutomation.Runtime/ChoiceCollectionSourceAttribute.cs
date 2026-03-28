using System;
using System.Collections;

namespace LocalAutomation.Runtime;

/// <summary>
/// Declares that a collection property should be rendered as a choice-backed multi-select editor.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ChoiceCollectionSourceAttribute : Attribute
{
    /// <summary>
    /// Creates choice-collection metadata for the provided source type.
    /// </summary>
    public ChoiceCollectionSourceAttribute(Type sourceType)
    {
        SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType));
    }

    /// <summary>
    /// Gets the source type that supplies the available choices for the annotated collection property.
    /// </summary>
    public Type SourceType { get; }
}

/// <summary>
/// Supplies the available values for a choice-backed collection property.
/// </summary>
public interface IChoiceCollectionSource
{
    /// <summary>
    /// Returns the available choices for the provided owning object and property name.
    /// </summary>
    IEnumerable GetChoices(object? component, string propertyName);
}
