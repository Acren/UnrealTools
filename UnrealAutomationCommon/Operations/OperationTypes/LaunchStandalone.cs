using System.Linq;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchStandalone : UnrealProcessOperation<Project>
    {
        /// <summary>
        /// Standalone launches reuse the selected editor build configuration when resolving the game executable path.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[] { typeof(OperationOptionTypes.BuildConfigurationOptions) });
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true);
            args.SetFlag("game");
            args.SetFlag("windowed");
            args.SetKeyValue("resx", "1920", false);
            args.SetKeyValue("resy", "1080", false);
            return new global::LocalAutomation.Runtime.Command(GetRequiredTargetEngineInstall(operationParameters).GetEditorExe(operationParameters), args.ToString());
        }
    }
}
