using System.Linq;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class GenerateProjectFiles : CommandProcessOperation<Project>
    {
        protected override System.Collections.Generic.IEnumerable<System.Type> GetDeclaredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target)
        {
            return base.GetDeclaredOptionSetTypes(target)
                .Concat(new[] { typeof(AdditionalArgumentsOptions) });
        }

        protected override global::LocalAutomation.Runtime.Command BuildCommand(global::LocalAutomation.Runtime.ValidatedOperationParameters operationParameters)
        {
            Project project = GetRequiredTarget(operationParameters);
            Arguments args = new();
            args.SetFlag("projectfiles");
            args.SetKeyPath("project", project.UProjectPath);
            args.SetFlag("game");
            args.SetFlag("rocket");
            args.SetFlag("progress");
            args.AddAdditionalArguments(operationParameters);
            return new global::LocalAutomation.Runtime.Command(GetRequiredTargetEngineInstall(operationParameters).GetUBTExe(), args.ToString());
        }
    }
}
