using System.IO;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class OpenEditor : Operation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(GetFileString(operationParameters.Project.ProjectDescriptor.GetEngineInstallDirectory(), operationParameters), GetArgsString(operationParameters.Project.UProjectPath, operationParameters));
        }

        protected override string GetOperationName()
        {
            return "Launch Editor";
        }

        private static string GetArgsString(string uProjectPath, OperationParameters operationParameters)
        {
            string argsString = "\"" + uProjectPath + "\"";
            CommandUtils.CombineArgs(ref argsString, UnrealArguments.MakeArguments(operationParameters).ToString());
            return argsString;
        }

        private static string GetFileString(string enginePath, OperationParameters operationParameters)
        {
            return Path.Combine(enginePath, "Engine", "Binaries", "Win64", operationParameters.Configuration == BuildConfiguration.DebugGame ? "UE4Editor-Win64-DebugGame.exe" : "UE4Editor.exe");
        }
    }
}
