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

        /// <summary>
        /// Returns a stable lock for one authored cached-build sequence. The lock deliberately uses the plan-known build
        /// identity instead of the runtime cache hash so a bodyless parent task can protect refresh, build, and copy-back
        /// steps before the cached project path exists.
        /// </summary>
        public static LocalAutomation.Runtime.ExecutionLock GetBuildWorkspaceCacheLock(string operationName, string role, string subjectName, EngineVersion engineVersion)
        {
            if (string.IsNullOrWhiteSpace(operationName))
            {
                throw new ArgumentException("Operation name is required for an Unreal build cache lock.", nameof(operationName));
            }

            if (string.IsNullOrWhiteSpace(role))
            {
                throw new ArgumentException("Build role is required for an Unreal build cache lock.", nameof(role));
            }

            if (string.IsNullOrWhiteSpace(subjectName))
            {
                throw new ArgumentException("Subject name is required for an Unreal build cache lock.", nameof(subjectName));
            }

            _ = engineVersion ?? throw new ArgumentNullException(nameof(engineVersion));
            return new LocalAutomation.Runtime.ExecutionLock($"unreal-build-cache:{operationName}:{role}:{subjectName}:UE{engineVersion.MajorMinorString}");
        }
    }
}
