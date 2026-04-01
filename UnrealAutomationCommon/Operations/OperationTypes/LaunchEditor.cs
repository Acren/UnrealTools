using System.Linq;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public abstract class LaunchEditor<T> : UnrealProcessOperation<T> where T : global::LocalAutomation.Runtime.OperationTarget, IEngineInstanceProvider
    {
        /// <summary>
        /// Editor launches use the selected build configuration to resolve the correct editor binary path.
        /// </summary>
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[] { typeof(OperationOptionTypes.BuildConfigurationOptions) });
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
        {
            return new global::LocalAutomation.Runtime.Command(GetRequiredTargetEngineInstall(operationParameters).GetEditorExe(operationParameters), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true).ToString());
        }
    }

    public class LaunchProjectEditor : LaunchEditor<Project> { }

    public class LaunchEditor : LaunchEditor<Engine> { }
}
