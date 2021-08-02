using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class GenerateProjectFiles : Operation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new Arguments();
            args.SetFlag("projectfiles");
            args.SetKeyPath("project", GetTarget(operationParameters).UProjectPath);
            args.SetFlag("game");
            args.SetFlag("rocket");
            args.SetFlag("progress");
            return new Command(GetTarget(operationParameters).GetEngineInstall().GetUBTExe(), args);
        }
    }
}
