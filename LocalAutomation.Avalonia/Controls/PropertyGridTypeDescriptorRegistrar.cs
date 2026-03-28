using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LocalAutomation.Runtime;
using PropertyModels.Collections;
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

        TypeDescriptor.AddProviderTransparent(new LocalAutomationMetadataTypeDescriptionProvider(TypeDescriptor.GetProvider(typeof(object))), typeof(object));
        _registered = true;
    }

    /// <summary>
    /// Adds Avalonia property-grid path browsing attributes to any property descriptors that expose shared path editor
    /// metadata from the runtime layer.
    /// </summary>
    private sealed class LocalAutomationMetadataTypeDescriptionProvider : TypeDescriptionProvider
    {
        /// <summary>
        /// Creates one provider layered on top of the previously registered descriptor provider.
        /// </summary>
        public LocalAutomationMetadataTypeDescriptionProvider(TypeDescriptionProvider parent)
            : base(parent)
        {
        }

        /// <summary>
        /// Returns a descriptor wrapper that injects Avalonia property-grid attributes when shared metadata requires it.
        /// </summary>
        public override ICustomTypeDescriptor GetTypeDescriptor(Type objectType, object? instance)
        {
            return new LocalAutomationMetadataTypeDescriptor(base.GetTypeDescriptor(objectType, instance) ?? EmptyTypeDescriptor.Instance);
        }
    }

    /// <summary>
    /// Wraps the parent descriptor so only properties carrying shared path metadata get augmented UI attributes.
    /// </summary>
    private sealed class LocalAutomationMetadataTypeDescriptor : CustomTypeDescriptor
    {
        /// <summary>
        /// Creates one path-aware descriptor wrapper around the upstream descriptor.
        /// </summary>
        public LocalAutomationMetadataTypeDescriptor(ICustomTypeDescriptor parent)
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
            PropertyDescriptor translatedDescriptor = descriptor;

            ChoiceCollectionSourceAttribute? choiceCollectionSource = descriptor.Attributes.OfType<ChoiceCollectionSourceAttribute>().FirstOrDefault();
            if (choiceCollectionSource != null)
            {
                translatedDescriptor = ChoiceCollectionPropertyDescriptorFactory.Create(translatedDescriptor, choiceCollectionSource);
            }

            PathEditorAttribute? pathEditor = descriptor.Attributes.OfType<PathEditorAttribute>().FirstOrDefault();
            if (pathEditor == null)
            {
                return translatedDescriptor;
            }

            PathBrowsableType pickerType = pathEditor.Kind == PathEditorKind.Directory
                ? PathBrowsableType.Directory
                : PathBrowsableType.File;

            return new AttributeMergingPropertyDescriptor(
                translatedDescriptor,
                new PathBrowsableAttribute(pickerType, false)
                {
                    Title = pathEditor.Title
                });
        }
    }

    /// <summary>
    /// Creates strongly typed checklist property descriptors from neutral runtime choice metadata.
    /// </summary>
    private static class ChoiceCollectionPropertyDescriptorFactory
    {
        /// <summary>
        /// Creates a checklist-backed descriptor for one collection property.
        /// </summary>
        public static PropertyDescriptor Create(PropertyDescriptor descriptor, ChoiceCollectionSourceAttribute choiceCollectionSource)
        {
            Type itemType = TryGetChoiceItemType(descriptor.PropertyType)
                ?? throw new InvalidOperationException($"Property '{descriptor.ComponentType.FullName}.{descriptor.Name}' must expose a generic collection type to use {nameof(ChoiceCollectionSourceAttribute)}.");

            Type descriptorType = typeof(ChoiceCollectionPropertyDescriptor<>).MakeGenericType(itemType);
            return (PropertyDescriptor)Activator.CreateInstance(descriptorType, descriptor, choiceCollectionSource)!;
        }

        /// <summary>
        /// Extracts the collection item type from a supported generic collection property.
        /// </summary>
        private static Type? TryGetChoiceItemType(Type propertyType)
        {
            if (propertyType.IsArray)
            {
                return propertyType.GetElementType();
            }

            if (propertyType.IsGenericType && propertyType.GetGenericArguments().Length == 1)
            {
                return propertyType.GetGenericArguments()[0];
            }

            Type? genericEnumerable = propertyType.GetInterfaces()
                .FirstOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return genericEnumerable?.GetGenericArguments()[0];
        }
    }

    /// <summary>
    /// Projects a choice-backed selection property into a PropertyModels checked-list while keeping the runtime option
    /// property as the source of truth for command preview, validation, and persistence.
    /// </summary>
    private sealed class ChoiceCollectionPropertyDescriptor<T> : PropertyDescriptor
    {
        private readonly ConditionalWeakTable<object, ChoiceCollectionBinding<T>> _bindings = new();
        private readonly PropertyDescriptor _inner;
        private readonly IChoiceCollectionSource _source;

        /// <summary>
        /// Creates one checklist-backed descriptor around the original runtime selection property.
        /// </summary>
        public ChoiceCollectionPropertyDescriptor(PropertyDescriptor inner, ChoiceCollectionSourceAttribute choiceCollectionSource)
            : base(inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _source = CreateSource(choiceCollectionSource);
        }

        /// <summary>
        /// Returns whether the wrapped property can be reset through the property grid.
        /// </summary>
        public override bool CanResetValue(object component) => _inner.CanResetValue(component);

        /// <summary>
        /// Gets the owning component type declared by the original runtime property.
        /// </summary>
        public override Type ComponentType => _inner.ComponentType;

        /// <summary>
        /// Returns the current checklist projection for the provided runtime component.
        /// </summary>
        public override object? GetValue(object? component)
        {
            if (component == null)
            {
                return null;
            }

            return GetBinding(component).CheckedList;
        }

        /// <summary>
        /// Keeps the checklist editor writable even when the underlying collection property itself is getter-only.
        /// </summary>
        public override bool IsReadOnly => false;

        /// <summary>
        /// Exposes the PropertyModels checked-list type so the generic property grid picks its checklist editor.
        /// </summary>
        public override Type PropertyType => typeof(CheckedList<T>);

        /// <summary>
        /// Delegates reset behavior to the original runtime property.
        /// </summary>
        public override void ResetValue(object component) => _inner.ResetValue(component);

        /// <summary>
        /// Replaces the current checklist projection and synchronizes its selected items back into the runtime property
        /// so downstream runtime notifications stay intact.
        /// </summary>
        public override void SetValue(object? component, object? value)
        {
            if (component == null)
            {
                return;
            }

            if (value is not CheckedList<T> checkedList)
            {
                throw new ArgumentException($"Property '{_inner.ComponentType.FullName}.{_inner.Name}' expects a value of type {typeof(CheckedList<T>).FullName}.", nameof(value));
            }

            GetBinding(component).ReplaceCheckedList(checkedList);
            OnValueChanged(component, EventArgs.Empty);
        }

        /// <summary>
        /// Delegates serialization checks to the original runtime property descriptor.
        /// </summary>
        public override bool ShouldSerializeValue(object component) => _inner.ShouldSerializeValue(component);

        /// <summary>
        /// Creates the choice source declared by the runtime attribute and validates its contract once.
        /// </summary>
        private static IChoiceCollectionSource CreateSource(ChoiceCollectionSourceAttribute choiceCollectionSource)
        {
            if (choiceCollectionSource == null)
            {
                throw new ArgumentNullException(nameof(choiceCollectionSource));
            }

            if (!typeof(IChoiceCollectionSource).IsAssignableFrom(choiceCollectionSource.SourceType))
            {
                throw new InvalidOperationException($"Choice source '{choiceCollectionSource.SourceType.FullName}' must implement {nameof(IChoiceCollectionSource)}.");
            }

            return (IChoiceCollectionSource)Activator.CreateInstance(choiceCollectionSource.SourceType)!;
        }

        /// <summary>
        /// Returns the cached binding for one live component instance so checklist event handlers stay attached to the
        /// same editor object across repeated property-grid reads.
        /// </summary>
        private ChoiceCollectionBinding<T> GetBinding(object component)
        {
            return _bindings.GetValue(component, CreateBinding);
        }

        /// <summary>
        /// Creates one component-specific binding that keeps the checklist projection and runtime selection property in sync.
        /// </summary>
        private ChoiceCollectionBinding<T> CreateBinding(object component)
        {
            return new ChoiceCollectionBinding<T>(component, _inner, _source, () => OnValueChanged(component, EventArgs.Empty));
        }
    }

    /// <summary>
    /// Synchronizes one runtime selection property with one checked-list projection instance.
    /// </summary>
    private sealed class ChoiceCollectionBinding<T>
    {
        private readonly object _component;
        private readonly IChoiceCollectionSource _source;
        private readonly Action _raiseValueChanged;
        private readonly PropertyDescriptor _propertyDescriptor;
        private bool _synchronizingFromCheckedList;

        /// <summary>
        /// Creates a binding between one runtime selection property and one checklist editor value.
        /// </summary>
        public ChoiceCollectionBinding(object component, PropertyDescriptor propertyDescriptor, IChoiceCollectionSource source, Action raiseValueChanged)
        {
            _component = component ?? throw new ArgumentNullException(nameof(component));
            _propertyDescriptor = propertyDescriptor ?? throw new ArgumentNullException(nameof(propertyDescriptor));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _raiseValueChanged = raiseValueChanged ?? throw new ArgumentNullException(nameof(raiseValueChanged));
            CheckedList = CreateCheckedList();
            SubscribeToCheckedList(CheckedList);
            AddOwnerPropertyChangedSubscription();
        }

        /// <summary>
        /// Gets the current checked-list projection exposed to the property grid.
        /// </summary>
        public CheckedList<T> CheckedList { get; private set; }

        /// <summary>
        /// Replaces the current checked-list projection and applies its selected items back into the runtime property.
        /// </summary>
        public void ReplaceCheckedList(CheckedList<T> checkedList)
        {
            if (checkedList == null)
            {
                throw new ArgumentNullException(nameof(checkedList));
            }

            if (ReferenceEquals(CheckedList, checkedList))
            {
                return;
            }

            UnsubscribeFromCheckedList(CheckedList);
            CheckedList = checkedList;
            SubscribeToCheckedList(CheckedList);
            SyncSourceListFromCheckedList();
        }

        /// <summary>
        /// Rebuilds the checklist when the runtime property changes outside the checked-list editor so the property grid
        /// can pull a fresh projection on its next read.
        /// </summary>
        private void HandleOwnerPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_synchronizingFromCheckedList)
            {
                return;
            }

            if (!string.IsNullOrEmpty(e.PropertyName) && !string.Equals(e.PropertyName, _propertyDescriptor.Name, StringComparison.Ordinal))
            {
                return;
            }

            UnsubscribeFromCheckedList(CheckedList);
            CheckedList = CreateCheckedList();
            SubscribeToCheckedList(CheckedList);
            _raiseValueChanged();
        }

        /// <summary>
        /// Applies checklist selection edits back into the runtime property so the owning option set can raise its
        /// normal property-change notifications.
        /// </summary>
        private void HandleSelectionChanged(object? sender, EventArgs e)
        {
            SyncSourceListFromCheckedList();
        }

        /// <summary>
        /// Creates a checked-list projection from the current source choices and selected runtime values.
        /// </summary>
        private CheckedList<T> CreateCheckedList()
        {
            T[] availableChoices = _source.GetChoices(_component, _propertyDescriptor.Name).Cast<T>().ToArray();
            T[] selectedChoices = GetSelectedItems().ToArray();
            return new CheckedList<T>(availableChoices, selectedChoices);
        }

        /// <summary>
        /// Returns the currently selected items from the runtime property value.
        /// </summary>
        private IEnumerable<T> GetSelectedItems()
        {
            if (_propertyDescriptor.GetValue(_component) is not IEnumerable selectedItems)
            {
                return Enumerable.Empty<T>();
            }

            return selectedItems.Cast<T>();
        }

        /// <summary>
        /// Hooks property-changed notifications so external writes refresh the cached checklist projection.
        /// </summary>
        private void AddOwnerPropertyChangedSubscription()
        {
            if (_component is INotifyPropertyChanged notifyPropertyChanged)
            {
                notifyPropertyChanged.PropertyChanged += HandleOwnerPropertyChanged;
            }
        }

        /// <summary>
        /// Subscribes to checklist selection changes so UI edits immediately update the runtime property.
        /// </summary>
        private void SubscribeToCheckedList(CheckedList<T> checkedList)
        {
            checkedList.SelectionChanged += HandleSelectionChanged;
        }

        /// <summary>
        /// Unsubscribes from checklist selection changes before the checklist projection is replaced.
        /// </summary>
        private void UnsubscribeFromCheckedList(CheckedList<T> checkedList)
        {
            checkedList.SelectionChanged -= HandleSelectionChanged;
        }

        /// <summary>
        /// Copies the currently checked items into the runtime selection property while suppressing the mirror refresh
        /// that would otherwise rebuild the checklist in the middle of a user edit.
        /// </summary>
        private void SyncSourceListFromCheckedList()
        {
            _synchronizingFromCheckedList = true;
            try
            {
                object replacementValue = CreateReplacementValue(CheckedList.Items.Cast<T>().ToArray());
                _propertyDescriptor.SetValue(_component, replacementValue);
            }
            finally
            {
                _synchronizingFromCheckedList = false;
            }
        }

        /// <summary>
        /// Creates the property value shape expected by the runtime option model from the checked-list selection.
        /// </summary>
        private object CreateReplacementValue(T[] selectedItems)
        {
            Type propertyType = _propertyDescriptor.PropertyType;
            if (propertyType.IsArray)
            {
                return selectedItems;
            }

            if (propertyType.IsAssignableFrom(selectedItems.GetType()))
            {
                return selectedItems;
            }

            throw new InvalidOperationException($"Property '{_propertyDescriptor.ComponentType.FullName}.{_propertyDescriptor.Name}' must be assignable from {typeof(T[]).FullName} to use {nameof(ChoiceCollectionSourceAttribute)}.");
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
