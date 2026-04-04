namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Centralizes typed Unreal lock requirements so operations opt into shared build serialization without constructing
    /// raw runtime lock keys directly.
    /// </summary>
    internal static class UnrealExecutionLocks
    {
        /// <summary>
        /// Serializes UBT/UAT-backed work inside one app instance so Unreal's shared writable build-rule artifacts are not
        /// regenerated concurrently by multiple callbacks.
        /// </summary>
        public static UnrealBuildLockRequirement GlobalBuild { get; } = new("global");
    }
}
