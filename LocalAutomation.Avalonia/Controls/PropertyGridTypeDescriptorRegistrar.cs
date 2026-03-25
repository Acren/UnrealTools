using System;
using System.ComponentModel;
using System.Linq;
using LocalAutomation.Runtime;
using PropertyModels.ComponentModel.DataAnnotations;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Registers Avalonia-specific descriptor translation once so every property grid can consume shared runtime metadata
/// without view-model wrappers or control-specific adaptation.
/// </summary>
public static class PropertyGridTypeDescriptorRegistrar
{
    private static bool _registered;

    /// <summary>
    /// Registers the global descriptor provider used by Avalonia property grids.
    /// </summary>
    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        TypeDescriptor.AddProviderTransparent(new PathEditorTypeDescriptionProvider(TypeDescriptor.GetProvider(typeof(object))), typeof(object));
        _registered = true;
    }

    /// <summary>
    /// Adds Avalonia property-grid path browsing attributes to any property descriptors that expose shared path editor
    /// metadata from the runtime layer.
    /// </summary>
    private sealed class PathEditorTypeDescriptionProvider : TypeDescriptionProvider
    {
        /// <summary>
        /// Creates one provider layered on top of the previously registered descriptor provider.
        /// </summary>
        public PathEditorTypeDescriptionProvider(TypeDescriptionProvider parent)
            : base(parent)
        {
        }

        /// <summary>
        /// Returns a descriptor wrapper that injects Avalonia property-grid attributes when shared metadata requires it.
        /// </summary>
        public override ICustomTypeDescriptor GetTypeDescriptor(Type objectType, object? instance)
        {
            return new PathEditorTypeDescriptor(base.GetTypeDescriptor(objectType, instance) ?? EmptyTypeDescriptor.Instance);
        }
    }

    /// <summary>
    /// Wraps the parent descriptor so only properties carrying shared path metadata get augmented UI attributes.
    /// </summary>
    private sealed class PathEditorTypeDescriptor : CustomTypeDescriptor
    {
        /// <summary>
        /// Creates one path-aware descriptor wrapper around the upstream descriptor.
        /// </summary>
        public PathEditorTypeDescriptor(ICustomTypeDescriptor parent)
            : base(parent)
        {
        }

        /// <summary>
        /// Returns the translated property descriptor list for callers that do not supply explicit attribute filters.
        /// </summary>
        public override PropertyDescriptorCollection GetProperties()
        {
            return GetProperties(Array.Empty<Attribute>());
        }

        /// <summary>
        /// Returns the translated property descriptor list while preserving all existing descriptor behavior.
        /// </summary>
        public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes)
        {
            PropertyDescriptorCollection baseProperties = base.GetProperties(attributes);
            PropertyDescriptor[] translatedProperties = baseProperties.Cast<PropertyDescriptor>()
                .Select(static descriptor => WrapIfNeeded(descriptor))
                .ToArray();
            return new PropertyDescriptorCollection(translatedProperties, true);
        }

        /// <summary>
        /// Returns the original descriptor when no path metadata is present, or a wrapped descriptor when Avalonia UI
        /// attributes need to be layered onto the shared runtime metadata.
        /// </summary>
        private static PropertyDescriptor WrapIfNeeded(PropertyDescriptor descriptor)
        {
            PathEditorAttribute? pathEditor = descriptor.Attributes.OfType<PathEditorAttribute>().FirstOrDefault();
            if (pathEditor == null)
            {
                return descriptor;
            }

            PathBrowsableType pickerType = pathEditor.Kind == PathEditorKind.Directory
                ? PathBrowsableType.Directory
                : PathBrowsableType.File;

            return new AttributeMergingPropertyDescriptor(
                descriptor,
                new PathBrowsableAttribute(pickerType, false)
                {
                    Title = pathEditor.Title
                });
        }
    }

    /// <summary>
    /// Provides a concrete empty descriptor for the rare case where the upstream provider returns null.
    /// </summary>
    private sealed class EmptyTypeDescriptor : CustomTypeDescriptor
    {
        /// <summary>
        /// Gets the shared empty descriptor instance.
        /// </summary>
        public static EmptyTypeDescriptor Instance { get; } = new();
    }

    /// <summary>
    /// Layers extra Avalonia-only attributes on top of an existing descriptor while delegating all value access and
    /// editing behavior to the original property implementation.
    /// </summary>
    private sealed class AttributeMergingPropertyDescriptor : PropertyDescriptor
    {
        private readonly PropertyDescriptor _inner;

        /// <summary>
        /// Creates a wrapper that preserves the original descriptor behavior and appends extra UI attributes.
        /// </summary>
        public AttributeMergingPropertyDescriptor(PropertyDescriptor inner, params Attribute[] extraAttributes)
            : base(inner, extraAttributes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanResetValue(object component) => _inner.CanResetValue(component);
        public override Type ComponentType => _inner.ComponentType;
        public override object? GetValue(object? component) => _inner.GetValue(component);
        public override bool IsReadOnly => _inner.IsReadOnly;
        public override Type PropertyType => _inner.PropertyType;
        public override void ResetValue(object component) => _inner.ResetValue(component);
        public override void SetValue(object? component, object? value) => _inner.SetValue(component, value);
        public override bool ShouldSerializeValue(object component) => _inner.ShouldSerializeValue(component);
    }
}
