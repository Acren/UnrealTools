using System.Linq;
using LocalAutomation.Extensions.Abstractions;
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

        protected override global::LocalAutomation.Runtime.Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            Engine engine = GetRequiredTargetEngineInstall(operationParameters);
            return new global::LocalAutomation.Runtime.Command(engine.GetEditorExe(operationParameters), UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true).ToString());
        }
    }

    [Operation(SortOrder = 4)]
    public class LaunchProjectEditor : LaunchEditor<Project> { }

    [Operation(SortOrder = 3)]
    public class LaunchEditor : LaunchEditor<Engine> { }
}
