using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class PackageProject : CommandProcessOperation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments arguments = UATArguments.MakeArguments(operationParameters);
            arguments.SetFlag("cook");
            arguments.SetFlag("stage");
            arguments.SetFlag("pak");
            arguments.SetFlag("package");
            arguments.SetFlag("nocompileeditor");
            arguments.SetFlag("archive");
            arguments.SetKeyPath("archivedirectory", GetOutputPath(operationParameters));
            return new Command(GetTarget(operationParameters).EngineInstall.GetRunUATPath(), arguments);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
