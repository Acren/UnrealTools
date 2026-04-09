using System;
using System.Collections.Generic;
using System.Linq;
using LocalAutomation.Runtime;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Builds operation descriptors from attributed runtime operation types so extensions do not need to maintain manual
/// operation lists by hand.
/// </summary>
public static class OperationDescriptorRegistration
{
    /// <summary>
    /// Registers all discoverable operations from the provided assembly into the supplied extension registry.
    /// </summary>
    public static void RegisterOperationsFromAssembly(IExtensionRegistry registry, IExtensionModule module, System.Reflection.Assembly assembly)
    {
        if (registry == null)
        {
            throw new ArgumentNullException(nameof(registry));
        }

        if (module == null)
        {
            throw new ArgumentNullException(nameof(module));
        }

        if (assembly == null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        /* Explicit attributes are the opt-in boundary for user-visible operations, so internal helper operations stay
           invisible even when they are public and reflectable. */
        IReadOnlyList<Type> operationTypes = assembly
            .GetTypes()
            .Where(IsDiscoverableOperationType)
            .OrderBy(type => type.GetCustomAttributes(typeof(OperationAttribute), inherit: false).Cast<OperationAttribute>().Single().SortOrder)
            .ThenBy(type => CreateOperation(type).OperationName, StringComparer.Ordinal)
            .ToList();

        foreach (Type operationType in operationTypes)
        {
            OperationAttribute metadata = operationType.GetCustomAttributes(typeof(OperationAttribute), inherit: false)
                .Cast<OperationAttribute>()
                .Single();
            Operation operation = CreateOperation(operationType);
            registry.RegisterOperation(new OperationDescriptor(
                id: BuildOperationId(module, operationType),
                displayName: operation.OperationName,
                operationType: operationType,
                supportedTargetTypes: GetSupportedTargetTypes(operationType),
                sortOrder: metadata.SortOrder));
        }
    }

    /// <summary>
    /// Extracts the runtime target type from generic operation base classes such as UnrealOperation&lt;T&gt; so extensions do
    /// not need to duplicate target-compatibility inference in each module.
    /// </summary>
    private static IReadOnlyList<Type> GetSupportedTargetTypes(Type operationType)
    {
        List<Type> supportedTargetTypes = new();
        Type? currentType = operationType;
        while (currentType != null)
        {
            if (currentType.IsGenericType)
            {
                Type[] genericArguments = currentType.GetGenericArguments();
                if (genericArguments.Length == 1 && typeof(IOperationTarget).IsAssignableFrom(genericArguments[0]))
                {
                    supportedTargetTypes.Add(genericArguments[0]);
                    break;
                }
            }

            currentType = currentType.BaseType;
        }

        if (supportedTargetTypes.Count == 0)
        {
            supportedTargetTypes.Add(typeof(IOperationTarget));
        }

        return supportedTargetTypes;
    }

    /// <summary>
    /// Returns whether one reflected runtime operation type is eligible for descriptor auto-registration.
    /// </summary>
    private static bool IsDiscoverableOperationType(Type operationType)
    {
        if (operationType == null || operationType.IsAbstract || !operationType.IsPublic)
        {
            return false;
        }

        if (!typeof(Operation).IsAssignableFrom(operationType))
        {
            return false;
        }

        /* The host still instantiates runtime operations through the default Activator path when building descriptors and
           when later executing by registered type, so discoverable operations must keep a public parameterless
           constructor. */
        return operationType.GetConstructor(Type.EmptyTypes) != null
            && operationType.GetCustomAttributes(typeof(OperationAttribute), inherit: false).Any();
    }

    /// <summary>
    /// Creates one runtime operation instance for descriptor display-name extraction.
    /// </summary>
    private static Operation CreateOperation(Type operationType)
    {
        return Operation.CreateOperation(operationType);
    }

    /// <summary>
    /// Builds the stable operation identifier from the owning module id and runtime type name so attributed operations do
    /// not need to repeat their serialized identifier strings in every class declaration.
    /// </summary>
    private static OperationId BuildOperationId(IExtensionModule module, Type operationType)
    {
        return new OperationId($"{module.Id}.operation.{operationType.Name}");
    }
}
