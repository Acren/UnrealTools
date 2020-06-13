using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public class OpenEditor : Operation
    {
        public static string GetArgsString(string UProjectPath, OperationParameters operationParameters)
        {
            string ArgsString = "\"" + UProjectPath + "\"";
            CommandUtils.CombineArgs(ref ArgsString, UnrealArguments.ToString(operationParameters));
            return ArgsString;
        }

        public static string GetFileString(string EnginePath, OperationParameters operationParameters)
        {
            return Path.Combine(EnginePath, "Engine", "Binaries", "Win64", operationParameters.Configuration == BuildConfiguration.DebugGame ? "UE4Editor-Win64-DebugGame.exe" : "UE4Editor.exe");
        }

        public override Command GetCommand(OperationParameters operationParameters)
        {
            return new Command(GetFileString(operationParameters.Project.ProjectDefinition.GetEngineInstallDirectory(), operationParameters), GetArgsString(operationParameters.Project.UProjectPath, operationParameters));
        }

        public override string GetOperationName()
        {
            return "Launch Editor";
        }
    }
}
