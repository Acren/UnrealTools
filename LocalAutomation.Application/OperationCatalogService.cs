using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Extensions.Abstractions;

namespace LocalAutomation.Application;

/// <summary>
/// Exposes catalog-backed queries for operations so hosts no longer need to inspect concrete extension modules or
/// maintain their own hardcoded operation lists.
/// </summary>
public sealed class OperationCatalogService
{
    private readonly ExtensionCatalog _catalog;

    /// <summary>
    /// Creates an operation catalog service around the shared extension catalog.
    /// </summary>
    public OperationCatalogService(ExtensionCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>
    /// Returns all registered operation descriptors in their configured sort order.
    /// </summary>
    public IReadOnlyList<OperationDescriptor> GetAllOperations()
    {
        return _catalog.OperationDescriptors
            .OrderBy(descriptor => descriptor.SortOrder)
            .ThenBy(descriptor => descriptor.DisplayName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns the descriptor with the provided stable identifier when one exists.
    /// </summary>
    public OperationDescriptor? GetOperation(string operationId)
    {
        return GetAllOperations().FirstOrDefault(descriptor => string.Equals(descriptor.Id, operationId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the descriptor for the provided runtime operation type when one exists.
    /// </summary>
    public OperationDescriptor? GetOperation(Type operationType)
    {
        return GetAllOperations().FirstOrDefault(descriptor => descriptor.OperationType == operationType);
    }

    /// <summary>
    /// Returns all registered runtime operation types in their configured sort order.
    /// </summary>
    public IReadOnlyList<Type> GetAllOperationTypes()
    {
        return GetAllOperations().Select(descriptor => descriptor.OperationType).ToList();
    }

    /// <summary>
    /// Returns the operations compatible with the provided target instance.
    /// </summary>
    public IReadOnlyList<OperationDescriptor> GetAvailableOperations(object? target)
    {
        if (target == null)
        {
            return Array.Empty<OperationDescriptor>();
        }

        return GetAllOperations().Where(descriptor => SupportsTarget(descriptor, target)).ToList();
    }

    /// <summary>
    /// Returns the runtime operation types compatible with the provided target instance.
    /// </summary>
    public IReadOnlyList<Type> GetAvailableOperationTypes(object? target)
    {
        return GetAvailableOperations(target).Select(descriptor => descriptor.OperationType).ToList();
    }

    /// <summary>
    /// Determines whether the descriptor supports the provided runtime target instance.
    /// </summary>
    private static bool SupportsTarget(OperationDescriptor descriptor, object target)
    {
        foreach (Type targetType in descriptor.SupportedTargetTypes)
        {
            if (targetType.IsInstanceOfType(target))
            {
                return true;
            }
        }

        return false;
    }
}
