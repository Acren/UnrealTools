using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchStandalone : UnrealProcessOperation<Project>
    {
        /// <summary>
        /// Standalone launches reuse the selected editor build configuration when resolving the game executable path.
        /// </summary>
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(OperationOptionTypes.BuildConfigurationOptions));
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true);
            args.SetFlag("game");
            args.SetFlag("windowed");
            args.SetKeyValue("resx", "1920", false);
            args.SetKeyValue("resy", "1080", false);
            return new global::LocalAutomation.Runtime.Command(GetTargetEngineInstall(operationParameters).GetEditorExe(operationParameters), args.ToString());
        }
    }
}
