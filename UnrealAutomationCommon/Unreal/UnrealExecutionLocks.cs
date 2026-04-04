namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Centralizes predefined Unreal execution locks so operations opt into shared build serialization without
    /// constructing raw runtime lock keys inline.
    /// </summary>
    internal static class UnrealExecutionLocks
    {
        /// <summary>
        /// Serializes UBT/UAT-backed work inside one app instance so Unreal's shared writable build-rule artifacts are not
        /// regenerated concurrently by multiple callbacks.
        /// </summary>
        public static LocalAutomation.Runtime.ExecutionLock GlobalBuild { get; } = new("unreal-build");
    }
}
