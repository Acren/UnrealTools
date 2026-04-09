using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    /// <summary>
    /// Builds the project's game target directly through Build.bat so package-only BuildCookRun passes can stage against
    /// an existing game receipt instead of holding the shared Unreal build lock through cook and package phases.
    /// </summary>
    internal sealed class BuildProjectTarget : BuildBatOperation<Project>
    {
        /// <summary>
        /// Uses the project's primary target name so direct UBT compilation produces the game receipt that staging later
        /// expects to find in Binaries/Win64.
        /// </summary>
        protected override void ConfigureBuildArguments(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, Arguments args)
        {
            Project project = GetRequiredTarget(operationParameters);

            args.SetArgument(project.Name);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.GetOptions<OperationOptionTypes.BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(project.UProjectPath);
        }

        /// <summary>
        /// Keeps deploy logs explicit about the direct game-target compile step that precedes package-only BuildCookRun.
        /// </summary>
        protected override string GetOperationName()
        {
            return "Build Project Target";
        }
    }
}
