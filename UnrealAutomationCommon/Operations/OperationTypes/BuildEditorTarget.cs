using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditorTarget : Operation<Project>
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new Arguments();
            args.SetArgument(GetTargetName(operationParameters) + "Editor");
            args.SetArgument("Win64");
            args.SetArgument(operationParameters.Configuration.ToString());
            args.SetPath(GetTarget(operationParameters).UProjectPath);
            return new Command(GetTarget(operationParameters).ProjectDescriptor.GetBuildPath(), args);
        }
    }
}
