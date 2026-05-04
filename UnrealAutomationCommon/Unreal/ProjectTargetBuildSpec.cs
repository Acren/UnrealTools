using System;
using System.IO;

namespace UnrealAutomationCommon.Unreal
{
    /// <summary>
    /// Describes the direct project target build identity shared by Build.bat arguments and UBT receipt lookup.
    /// </summary>
    internal readonly struct ProjectTargetBuildSpec
    {
        /// <summary>
        /// The project target platform currently used by direct project target build operations.
        /// </summary>
        public const string DefaultPlatform = "Win64";

        /// <summary>
        /// Creates one immutable direct project target build identity.
        /// </summary>
        public ProjectTargetBuildSpec(string targetName, string platform, BuildConfiguration configuration)
        {
            TargetName = RequireText(targetName, nameof(targetName), "Target name is required for a project target build.");
            Platform = RequireText(platform, nameof(platform), "Platform is required for a project target build.");
            Configuration = configuration;
        }

        /// <summary>
        /// Gets the Unreal target name passed as the first Build.bat argument and written into the target receipt.
        /// </summary>
        public string TargetName { get; }

        /// <summary>
        /// Gets the Unreal target platform passed to Build.bat and used in the receipt path.
        /// </summary>
        public string Platform { get; }

        /// <summary>
        /// Gets the Unreal target configuration passed to Build.bat and written into the target receipt.
        /// </summary>
        public BuildConfiguration Configuration { get; }

        /// <summary>
        /// Creates the direct game-target build identity used before package-only BuildCookRun invocations.
        /// </summary>
        public static ProjectTargetBuildSpec ForGameTarget(Project project, BuildConfiguration configuration)
        {
            _ = project ?? throw new ArgumentNullException(nameof(project));
            return new ProjectTargetBuildSpec(project.Name, DefaultPlatform, configuration);
        }

        /// <summary>
        /// Resolves the UBT target receipt path that belongs to this direct project target build.
        /// </summary>
        public string GetReceiptPath(Project project)
        {
            _ = project ?? throw new ArgumentNullException(nameof(project));
            string receiptFileName = Configuration == BuildConfiguration.Development
                ? $"{TargetName}.target"
                : $"{TargetName}-{Platform}-{Configuration}.target";
            return Path.Combine(project.ProjectPath, "Binaries", Platform, receiptFileName);
        }

        /// <summary>
        /// Returns non-empty configuration text or throws with a parameter-specific message.
        /// </summary>
        private static string RequireText(string value, string parameterName, string message)
        {
            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException(message, parameterName)
                : value;
        }
    }
}
