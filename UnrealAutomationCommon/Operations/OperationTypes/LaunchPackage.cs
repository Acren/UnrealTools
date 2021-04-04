using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon.Operations.OperationTypes
{
    public class LaunchPackage : Operation
    {
        protected override Command BuildCommand(OperationParameters operationParameters)
        {
            return new Command(operationParameters.Project.GetStagedPackageExecutablePath(), UnrealArguments.MakeArguments(operationParameters));
        }

        protected override string GetOperationName()
        {
            return "Launch Package";
        }
    }
}
