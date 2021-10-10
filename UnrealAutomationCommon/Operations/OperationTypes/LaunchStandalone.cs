using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchStandalone : UnrealProcessOperation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, GetOutputPath(operationParameters), true);
            args.SetFlag("game");
            args.SetFlag("windowed");
            args.SetKeyValue("resx", "1920", false);
            args.SetKeyValue("resy", "1080", false);
            return new Command(GetTarget(operationParameters).EngineInstall.GetEditorExe(operationParameters), args);
        }
    }
}