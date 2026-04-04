using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Centralizes the runtime's mapping from typed execution lock requirements to internal lock keys.
/// </summary>
internal static class ExecutionLockKeys
{
    /// <summary>
    /// Resolves one typed lock requirement to the internal dictionary key used by the shared semaphore table.
    /// </summary>
    public static string GetKey(ExecutionLockRequirement requirement)
    {
        return requirement switch
        {
            null => throw new ArgumentNullException(nameof(requirement)),
            _ => requirement.Key
        };
    }
}
