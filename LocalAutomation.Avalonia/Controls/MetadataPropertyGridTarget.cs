using System;
using System.ComponentModel;
using System.Linq;
using PropertyModels.ComponentModel.DataAnnotations;
using LocalAutomation.Runtime;

namespace LocalAutomation.Avalonia.Controls;

/// <summary>
/// Wraps an existing settings or options object and augments neutral shared metadata with Avalonia-specific property
/// grid attributes without duplicating the underlying property declarations.
/// </summary>
public sealed class MetadataPropertyGridTarget : ICustomTypeDescriptor, INotifyPropertyChanged, IDisposable
{
    private readonly object _source;
    private readonly PropertyDescriptorCollection _properties;

    /// <summary>
    /// Creates a metadata-augmented property-grid target around the provided source object.
    /// </summary>
    public MetadataPropertyGridTarget(object source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _properties = new PropertyDescriptorCollection(TypeDescriptor.GetProperties(_source)
            .Cast<PropertyDescriptor>()
            .Select(descriptor => new SourcePropertyDescriptor(_source, descriptor, BuildUiAttributes(descriptor)))
            .ToArray(), true);

        if (_source is INotifyPropertyChanged notifyingSource)
        {
            notifyingSource.PropertyChanged += HandleSourcePropertyChanged;
        }
    }

    /// <summary>
    /// Raised when the wrapped source reports a property change.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Returns the wrapped properties with Avalonia-specific metadata layered on top of the shared descriptors.
    /// </summary>
    public PropertyDescriptorCollection GetProperties()
    {
        return _properties;
    }

    /// <summary>
    /// Returns the wrapped properties with Avalonia-specific metadata layered on top of the shared descriptors.
    /// </summary>
    public PropertyDescriptorCollection GetProperties(Attribute[]? attributes)
    {
        return attributes == null || attributes.Length == 0 ? _properties : new PropertyDescriptorCollection(
            _properties.Cast<PropertyDescriptor>().Where(descriptor => descriptor.Attributes.Contains(attributes)).ToArray(),
            true);
    }

    /// <summary>
    /// Stops forwarding property-changed notifications from the wrapped source.
    /// </summary>
    public void Dispose()
    {
        if (_source is INotifyPropertyChanged notifyingSource)
        {
            notifyingSource.PropertyChanged -= HandleSourcePropertyChanged;
        }
    }

    AttributeCollection ICustomTypeDescriptor.GetAttributes() => TypeDescriptor.GetAttributes(_source);
    string? ICustomTypeDescriptor.GetClassName() => TypeDescriptor.GetClassName(_source);
    string? ICustomTypeDescriptor.GetComponentName() => TypeDescriptor.GetComponentName(_source);
    TypeConverter? ICustomTypeDescriptor.GetConverter() => TypeDescriptor.GetConverter(_source);
    EventDescriptor? ICustomTypeDescriptor.GetDefaultEvent() => TypeDescriptor.GetDefaultEvent(_source);
    PropertyDescriptor? ICustomTypeDescriptor.GetDefaultProperty() => TypeDescriptor.GetDefaultProperty(_source);
    object? ICustomTypeDescriptor.GetEditor(Type editorBaseType) => TypeDescriptor.GetEditor(_source, editorBaseType);
    EventDescriptorCollection ICustomTypeDescriptor.GetEvents() => TypeDescriptor.GetEvents(_source);
    EventDescriptorCollection ICustomTypeDescriptor.GetEvents(Attribute[]? attributes) => TypeDescriptor.GetEvents(_source, attributes);
    object ICustomTypeDescriptor.GetPropertyOwner(PropertyDescriptor? pd) => _source;

    /// <summary>
    /// Translates neutral shared metadata into the concrete attributes expected by the Avalonia property grid.
    /// </summary>
    private static Attribute[] BuildUiAttributes(PropertyDescriptor descriptor)
    {
        PathEditorAttribute? pathEditor = descriptor.Attributes.OfType<PathEditorAttribute>().FirstOrDefault();
        if (pathEditor == null)
        {
            return Array.Empty<Attribute>();
        }

        PathBrowsableType pickerType = pathEditor.Kind == PathEditorKind.Directory
            ? PathBrowsableType.Directory
            : PathBrowsableType.File;

        return new Attribute[]
        {
            new PathBrowsableAttribute(pickerType, false)
            {
                Title = pathEditor.Title
            }
        };
    }

    /// <summary>
    /// Forwards source property changes so the property grid can refresh against the wrapped object naturally.
    /// </summary>
    private void HandleSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
    }

    /// <summary>
    /// Redirects property-grid reads and writes to the wrapped source object while preserving the underlying property
    /// metadata and any UI-specific attributes layered on top.
    /// </summary>
    private sealed class SourcePropertyDescriptor : PropertyDescriptor
    {
        private readonly object _source;
        private readonly PropertyDescriptor _inner;

        /// <summary>
        /// Creates one redirecting property descriptor for the wrapped source object.
        /// </summary>
        public SourcePropertyDescriptor(object source, PropertyDescriptor inner, Attribute[] extraAttributes)
            : base(inner, extraAttributes)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanResetValue(object component) => _inner.CanResetValue(_source);
        public override Type ComponentType => _source.GetType();
        public override object? GetValue(object? component) => _inner.GetValue(_source);
        public override bool IsReadOnly => _inner.IsReadOnly;
        public override Type PropertyType => _inner.PropertyType;
        public override void ResetValue(object component) => _inner.ResetValue(_source);

        public override void SetValue(object? component, object? value)
        {
            _inner.SetValue(_source, value);
            OnValueChanged(component, EventArgs.Empty);
        }

        public override bool ShouldSerializeValue(object component) => _inner.ShouldSerializeValue(_source);
    }
}
