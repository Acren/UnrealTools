using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class BuildEditorTarget : Operation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            Arguments args = new Arguments();
            args.AddArgument(operationParameters.Project.Name + "Editor");
            args.AddArgument("Win64");
            args.AddArgument(operationParameters.Configuration.ToString());
            args.AddPath(operationParameters.Project.UProjectPath);
            return new Command(operationParameters.Project.ProjectDescriptor.GetBuildPath(), args);
        }
    }
}
