using System;
using LocalAutomation.Runtime;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Declares that an operation requires the shared in-process Unreal build lock while invoking tooling that can race on
    /// global UnrealBuildTool or AutomationTool writable state.
    /// </summary>
    public sealed class UnrealBuildLockRequirement : ExecutionLockRequirement, IEquatable<UnrealBuildLockRequirement>
    {
        public UnrealBuildLockRequirement(string scopeName)
        {
            ScopeName = string.IsNullOrWhiteSpace(scopeName)
                ? throw new ArgumentException("Build lock scope name is required.", nameof(scopeName))
                : scopeName;
        }

        public string ScopeName { get; }

        /// <summary>
        /// Maps the typed Unreal build lock to the shared runtime key used by the in-process semaphore table.
        /// </summary>
        public override string Key
        {
            get { return "unreal-build:" + ScopeName; }
        }

        /// <summary>
        /// Treats two Unreal build lock requirements as the same lock when they target the same scope name so duplicate
        /// declarations collapse before the runtime acquires semaphores.
        /// </summary>
        public bool Equals(UnrealBuildLockRequirement? other)
        {
            return other != null && string.Equals(ScopeName, other.ScopeName, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as UnrealBuildLockRequirement);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(ScopeName);
        }
    }
}
