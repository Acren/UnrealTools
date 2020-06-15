using System.IO;

namespace UnrealAutomationCommon.Operations
{
    public class OpenEditor : Operation
    {
        public override Command GetCommand(OperationParameters operationParameters)
        {
            return new Command(GetFileString(operationParameters.Project.ProjectDefinition.GetEngineInstallDirectory(), operationParameters), GetArgsString(operationParameters.Project.UProjectPath, operationParameters));
        }

        protected override string GetOperationName()
        {
            return "Launch Editor";
        }

        private static string GetArgsString(string uProjectPath, OperationParameters operationParameters)
        {
            string ArgsString = "\"" + uProjectPath + "\"";
            CommandUtils.CombineArgs(ref ArgsString, UnrealArguments.MakeArguments(operationParameters).ToString());
            return ArgsString;
        }

        private static string GetFileString(string enginePath, OperationParameters operationParameters)
        {
            return Path.Combine(enginePath, "Engine", "Binaries", "Win64", operationParameters.Configuration == BuildConfiguration.DebugGame ? "UE4Editor-Win64-DebugGame.exe" : "UE4Editor.exe");
        }
    }
}
