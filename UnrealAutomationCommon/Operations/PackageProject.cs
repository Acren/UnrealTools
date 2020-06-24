using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnrealAutomationCommon.Operations
{
    public class PackageProject : Operation
    {
        public override Command GetCommand(OperationParameters operationParameters)
        {
            Arguments Arguments = UATArguments.MakeArguments(operationParameters);
            Arguments.AddFlag("cook");
            Arguments.AddFlag("stage");
            Arguments.AddFlag("pak");
            return new Command(operationParameters.Project.ProjectDefinition.GetRunUAT(), Arguments);
        }

        protected override string GetOperationName()
        {
            return "Package Project";
        }
    }
}
