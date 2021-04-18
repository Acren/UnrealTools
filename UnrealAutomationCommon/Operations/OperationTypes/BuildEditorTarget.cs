using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditorTarget : ProjectOperation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new Arguments();
            args.AddArgument(GetTargetName(operationParameters) + "Editor");
            args.AddArgument("Win64");
            args.AddArgument(operationParameters.Configuration.ToString());
            args.AddPath(GetProject(operationParameters).UProjectPath);
            return new Command(GetProject(operationParameters).ProjectDescriptor.GetBuildPath(), args);
        }
    }
}
