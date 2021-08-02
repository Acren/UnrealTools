using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class GenerateProjectFiles : ProjectOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new Arguments();
            args.SetFlag("projectfiles");
            args.SetKeyPath("project", GetProject(operationParameters).UProjectPath);
            args.SetFlag("game");
            args.SetFlag("rocket");
            args.SetFlag("progress");
            return new Command(GetProject(operationParameters).GetEngineInstall().GetUBTExe(), args);
        }
    }
}
