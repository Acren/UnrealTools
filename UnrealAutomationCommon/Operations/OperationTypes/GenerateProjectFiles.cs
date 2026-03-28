using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class GenerateProjectFiles : CommandProcessOperation<Project>
    {
        protected override global::LocalAutomation.Runtime.Command BuildCommand(UnrealOperationParameters operationParameters)
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
