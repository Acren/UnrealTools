using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildPlugin : CommandProcessOperation<Plugin>
    {
        // Direct Build.bat plugin compilation needs a host project and only applies to code plugins.
        public override string CheckRequirementsSatisfied(OperationParameters operationParameters)
        {
            string requirementsError = base.CheckRequirementsSatisfied(operationParameters);
            if (requirementsError != null)
            {
                return requirementsError;
            }

            Plugin plugin = GetTarget(operationParameters);
            if (plugin.IsBlueprintOnly)
            {
                return "Build Plugin only supports code plugins";
            }

            Project hostProject = plugin.HostProject;
            if (hostProject == null || !hostProject.IsValid)
            {
                return "Build Plugin requires the plugin to live inside a valid host project";
            }

            if (hostProject.EngineInstance == null)
            {
                return "Build Plugin could not resolve a host project engine install";
            }

            return null;
        }

        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new();
            Plugin plugin = GetTarget(operationParameters);
            Project hostProject = plugin.HostProject;
            UbtCompiler compiler = operationParameters.RequestOptions<UbtCompilerOptions>().Compiler;

            // Match Unreal's direct plugin build flow: editor target, platform, configuration, host project, then plugin path.
            args.SetArgument(GetTargetEngineInstall(operationParameters).BaseEditorName);
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.RequestOptions<BuildConfigurationOptions>().Configuration.ToString());
            args.SetPath(hostProject.UProjectPath);
            args.SetKeyPath("plugin", plugin.UPluginPath);

            // Only emit an explicit compiler flag when the user has opted out of the engine default behavior.
            if (compiler != UbtCompiler.Default)
            {
                args.SetKeyValue("Compiler", compiler.ToString());
            }

            args.AddAdditionalArguments(operationParameters);
            return new Command(GetTargetEngineInstall(operationParameters).GetBuildPath(), args);
        }
    }
}
