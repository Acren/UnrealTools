using System;
using LocalAutomation.Extensions.Abstractions;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    [Operation(SortOrder = 1)]
    public class BuildEditorTarget : BuildBatOperation<Project>
    {
        // Build the project's editor target directly through Build.bat so direct UBT overrides are honored.
        protected override void ConfigureBuildArguments(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters, Arguments args)
        {
            Project project = GetRequiredTarget(operationParameters);
            string editorTargetName = ResolveEditorTargetName(project);

            // Build.bat forwards these arguments directly to UBT, so a compiler override is reliable here.
            args.SetArgument(editorTargetName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.GetOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(project.UProjectPath);
        }

        /// <summary>
        /// Resolves the editor target name that UBT accepts for both source projects and hybrid content-only projects.
        /// </summary>
        private static string ResolveEditorTargetName(Project project)
        {
            ProjectDescriptor descriptor = project.ProjectDescriptor
                ?? throw new InvalidOperationException("Build Editor Target requires a loaded project descriptor.");

            // Explicit editor modules own their target name directly because generated project files use the same module
            // declaration when identifying project editor targets.
            ModuleDeclaration? editorModule = descriptor.Modules.Find(module => string.Equals(module.Type, "Editor", StringComparison.OrdinalIgnoreCase));
            if (editorModule != null)
            {
                return editorModule.Name;
            }

            // Source projects without an editor module conventionally append Editor to their primary module target.
            if (descriptor.Modules.Count > 0)
            {
                return descriptor.Modules[0].Name + "Editor";
            }

            // Hybrid content-only projects get temporary targets named from the .uproject file when UBT detects enabled
            // code plugins, so direct editor builds must use the same target name that Unreal will generate.
            return project.Name + "Editor";
        }
    }
}
