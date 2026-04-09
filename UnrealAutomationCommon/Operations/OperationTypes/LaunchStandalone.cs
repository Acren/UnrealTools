using System.Linq;
using LocalAutomation.Extensions.Abstractions;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    [Operation(SortOrder = 5)]
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

        protected override global::LocalAutomation.Runtime.Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true);
            args.SetFlag("game");
            args.SetFlag("windowed");
            args.SetKeyValue("resx", "1920", false);
            args.SetKeyValue("resy", "1080", false);
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            return new global::LocalAutomation.Runtime.Command(engine.GetEditorExe(operationParameters), args.ToString());
        }
    }
}
