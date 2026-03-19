using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using UnrealAutomationCommon;
using UnrealAutomationCommon.Operations;

namespace LocalAutomation.Extensions.Unreal;

/// <summary>
/// Projects Unreal operation option wrappers into property-grid-friendly descriptors while leaving ordinary CLR
/// properties untouched.
/// </summary>
public sealed class OperationOptionsTypeDescriptionProvider : TypeDescriptionProvider
{
    private static readonly TypeDescriptionProvider BaseProvider = TypeDescriptor.GetProvider(typeof(OperationOptions));

    /// <summary>
    /// Creates a provider that wraps the default provider for <see cref="OperationOptions"/>.
    /// </summary>
    public OperationOptionsTypeDescriptionProvider()
        : base(BaseProvider)
    {
    }

    /// <summary>
    /// Returns a custom descriptor that unwraps <see cref="Option{T}"/> properties into editable value properties.
    /// </summary>
    public override ICustomTypeDescriptor GetTypeDescriptor(Type objectType, object? instance)
    {
        return new OperationOptionsTypeDescriptor(base.GetTypeDescriptor(objectType, instance));
    }

    /// <summary>
    /// Wraps the default type descriptor and replaces option-wrapper property descriptors when needed.
    /// </summary>
    private sealed class OperationOptionsTypeDescriptor : CustomTypeDescriptor
    {
        /// <summary>
        /// Creates a wrapper over the default type descriptor.
        /// </summary>
        public OperationOptionsTypeDescriptor(ICustomTypeDescriptor parent)
            : base(parent)
        {
        }

        /// <summary>
        /// Returns projected properties for the property grid.
        /// </summary>
        public override PropertyDescriptorCollection GetProperties()
        {
            return GetProperties(Array.Empty<Attribute>());
        }

        /// <summary>
        /// Returns projected properties for the property grid.
        /// </summary>
        public override PropertyDescriptorCollection GetProperties(Attribute[]? attributes)
        {
            List<PropertyDescriptor> descriptors = new();
            foreach (PropertyDescriptor property in base.GetProperties(attributes).Cast<PropertyDescriptor>())
            {
                if (property.Attributes.OfType<BrowsableAttribute>().FirstOrDefault()?.Browsable == false)
                {
                    continue;
                }

                descriptors.Add(TryCreateWrappedDescriptor(property) ?? new DisplayNamePropertyDescriptor(property));
            }

            return new PropertyDescriptorCollection(descriptors.ToArray(), true);
        }

        /// <summary>
        /// Returns a projected property descriptor when the original property wraps an <see cref="Option{T}"/>.
        /// </summary>
        private static PropertyDescriptor? TryCreateWrappedDescriptor(PropertyDescriptor property)
        {
            if (!property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(Option<>))
            {
                return null;
            }

            PropertyInfo? valueProperty = property.PropertyType.GetProperty(nameof(Option<bool>.Value));
            return valueProperty == null ? null : new WrappedOptionPropertyDescriptor(property, valueProperty);
        }
    }

    /// <summary>
    /// Re-exposes a normal property with a friendlier default display name when no explicit display metadata exists.
    /// </summary>
    private sealed class DisplayNamePropertyDescriptor : PropertyDescriptor
    {
        private readonly AttributeCollection _attributes;
        private readonly PropertyDescriptor _inner;

        /// <summary>
        /// Creates a wrapper around an existing property descriptor.
        /// </summary>
        public DisplayNamePropertyDescriptor(PropertyDescriptor inner)
            : base(inner.Name, BuildAttributes(inner))
        {
            _inner = inner;
            _attributes = new AttributeCollection(BuildAttributes(inner));
        }

        /// <summary>
        /// Gets the projected attributes exposed to the property grid.
        /// </summary>
        public override AttributeCollection Attributes => _attributes;

        public override Type ComponentType => _inner.ComponentType;
        public override bool IsReadOnly => _inner.IsReadOnly;
        public override Type PropertyType => _inner.PropertyType;
        public override bool CanResetValue(object component) => _inner.CanResetValue(component);
        public override object? GetValue(object? component) => _inner.GetValue(component);
        public override void ResetValue(object component) => _inner.ResetValue(component);
        public override void SetValue(object? component, object? value) => _inner.SetValue(component, value);
        public override bool ShouldSerializeValue(object component) => _inner.ShouldSerializeValue(component);

        /// <summary>
        /// Builds the property-grid-facing attribute list for a normal property.
        /// </summary>
        private static Attribute[] BuildAttributes(MemberDescriptor property)
        {
            List<Attribute> attributes = property.Attributes.Cast<Attribute>()
                .Where(attribute => attribute is not BrowsableAttribute)
                .ToList();

            if (attributes.OfType<DisplayNameAttribute>().FirstOrDefault() == null)
            {
                attributes.Add(new DisplayNameAttribute(property.Name.SplitWordsByUppercase()));
            }

            return attributes.ToArray();
        }
    }

    /// <summary>
    /// Re-exposes a wrapped <see cref="Option{T}"/> as a normal editable property for the property grid.
    /// </summary>
    private sealed class WrappedOptionPropertyDescriptor : PropertyDescriptor
    {
        private readonly AttributeCollection _attributes;
        private readonly PropertyDescriptor _inner;
        private readonly PropertyInfo _valueProperty;

        /// <summary>
        /// Creates a new projected descriptor around the wrapped option property.
        /// </summary>
        public WrappedOptionPropertyDescriptor(PropertyDescriptor inner, PropertyInfo valueProperty)
            : base(inner.Name, BuildAttributes(inner))
        {
            _inner = inner;
            _valueProperty = valueProperty;
            _attributes = new AttributeCollection(BuildAttributes(inner));
        }

        /// <summary>
        /// Gets the projected attributes exposed to the property grid.
        /// </summary>
        public override AttributeCollection Attributes => _attributes;

        /// <summary>
        /// Gets the original component type.
        /// </summary>
        public override Type ComponentType => _inner.ComponentType;

        /// <summary>
        /// The projected value is editable because the wrapper exposes a writable <c>Value</c> property.
        /// </summary>
        public override bool IsReadOnly => !_valueProperty.CanWrite;

        /// <summary>
        /// Gets the unwrapped property type.
        /// </summary>
        public override Type PropertyType => _valueProperty.PropertyType;

        public override bool CanResetValue(object component) => false;

        /// <summary>
        /// Reads the unwrapped value from the wrapped option property.
        /// </summary>
        public override object? GetValue(object? component)
        {
            object? option = _inner.GetValue(component);
            return option == null ? null : _valueProperty.GetValue(option);
        }

        public override void ResetValue(object component)
        {
        }

        /// <summary>
        /// Writes the unwrapped value back into the wrapped option property.
        /// </summary>
        public override void SetValue(object? component, object? value)
        {
            object? option = _inner.GetValue(component);
            if (option == null)
            {
                return;
            }

            _valueProperty.SetValue(option, value);
            OnValueChanged(component, EventArgs.Empty);
        }

        public override bool ShouldSerializeValue(object component) => true;

        /// <summary>
        /// Builds the property-grid-facing attribute list for a wrapped option property.
        /// </summary>
        private static Attribute[] BuildAttributes(MemberDescriptor property)
        {
            List<Attribute> attributes = property.Attributes.Cast<Attribute>()
                .Where(attribute => attribute is not BrowsableAttribute)
                .ToList();

            if (attributes.OfType<DisplayNameAttribute>().FirstOrDefault() == null)
            {
                attributes.Add(new DisplayNameAttribute(property.Name.SplitWordsByUppercase()));
            }

            return attributes.ToArray();
        }
    }
}
