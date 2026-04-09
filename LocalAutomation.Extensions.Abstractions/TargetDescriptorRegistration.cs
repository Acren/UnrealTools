using System;
using System.Linq;
using System.Reflection;
using LocalAutomation.Runtime;

namespace LocalAutomation.Extensions.Abstractions;

/// <summary>
/// Builds target descriptors from attributed runtime target types so extensions do not need to maintain manual target
/// lists by hand.
/// </summary>
public static class TargetDescriptorRegistration
{
    /// <summary>
    /// Registers all discoverable targets from the provided assembly into the supplied extension registry.
    /// </summary>
    public static void RegisterTargetsFromAssembly(IExtensionRegistry registry, IExtensionModule module, Assembly assembly)
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

        /* Explicit attributes are the opt-in boundary for user-visible targets, so supporting runtime helper types can
           live beside real targets in the same assembly without being registered accidentally. */
        foreach (Type targetType in assembly.GetTypes().Where(IsDiscoverableTargetType).OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            TargetAttribute metadata = targetType.GetCustomAttributes(typeof(TargetAttribute), inherit: false)
                .Cast<TargetAttribute>()
                .Single();
            registry.RegisterTarget(new TargetDescriptor(
                id: BuildTargetId(module, targetType),
                displayName: string.IsNullOrWhiteSpace(metadata.DisplayName) ? targetType.Name : metadata.DisplayName,
                targetType: targetType));
        }
    }

    /// <summary>
    /// Returns whether one reflected runtime target type is eligible for descriptor auto-registration.
    /// </summary>
    private static bool IsDiscoverableTargetType(Type targetType)
    {
        if (targetType == null || targetType.IsAbstract || !targetType.IsPublic)
        {
            return false;
        }

        if (!typeof(IOperationTarget).IsAssignableFrom(targetType))
        {
            return false;
        }

        /* Hosts and path factories create targets through the default Activator path today, so discoverable target types
           must keep a public constructor that accepts the serialized target path. */
        return targetType.GetConstructor(new[] { typeof(string) }) != null
            && targetType.GetCustomAttributes(typeof(TargetAttribute), inherit: false).Any();
    }

    /// <summary>
    /// Builds the stable target identifier from the owning module id and runtime type name to avoid repeating descriptor
    /// strings on every annotated target class.
    /// </summary>
    private static TargetTypeId BuildTargetId(IExtensionModule module, Type targetType)
    {
        return new TargetTypeId($"{module.Id}.{targetType.Name.ToLowerInvariant()}");
    }
}
