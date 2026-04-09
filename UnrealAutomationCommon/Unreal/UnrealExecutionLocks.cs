using System;
using System.IO;

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

        /// <summary>
        /// Returns the per-engine AutomationTool lock so only one RunUAT invocation can target the same engine install at
        /// a time, while different engine installs still run in parallel.
        /// </summary>
        public static LocalAutomation.Runtime.ExecutionLock GetAutomationToolLock(Engine engine)
        {
            if (engine == null)
            {
                throw new ArgumentNullException(nameof(engine));
            }

            /* AutomationTool enforces a singleton per engine install, so the lock key must follow the resolved engine path
               rather than just the version string because separate installs can share the same version number. */
            string normalizedEnginePath = Path.GetFullPath(engine.TargetPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            return new LocalAutomation.Runtime.ExecutionLock($"unreal-automationtool:{normalizedEnginePath}");
        }
    }
}
