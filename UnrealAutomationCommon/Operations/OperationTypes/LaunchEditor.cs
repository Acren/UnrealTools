using System.IO;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchEditor : Operation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(UnrealPaths.GetEditorExe(operationParameters.Project.ProjectDescriptor.GetEngineInstallDirectory(), operationParameters), UnrealArguments.MakeArguments(operationParameters, true));
        }

    }
}
