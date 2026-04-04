using System;

namespace LocalAutomation.Runtime;

/// <summary>
/// Centralizes the runtime's mapping from typed execution lock requirements to internal lock keys.
/// </summary>
internal static class ExecutionLockKeys
{
    /// <summary>
    /// Resolves one typed execution lock to the internal dictionary key used by the shared semaphore table.
    /// </summary>
    public static string GetKey(ExecutionLock executionLock)
    {
        return executionLock switch
        {
            null => throw new ArgumentNullException(nameof(executionLock)),
            _ => executionLock.Key
        };
    }
}
