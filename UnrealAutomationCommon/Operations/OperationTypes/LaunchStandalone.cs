using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchStandalone : Operation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = UnrealArguments.MakeArguments(operationParameters, true);
            args.AddFlag("game");
            args.AddFlag("windowed");
            args.AddKeyValue("resx", "1920");
            args.AddKeyValue("resy", "1080");
            return new Command(UnrealPaths.GetEditorExe(operationParameters.Project.ProjectDescriptor.GetEngineInstallDirectory(), operationParameters), args);
        }
    }
}
