using System;
using System.Collections.Generic;
using System.Text;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public class UATArguments
    {
        public static Arguments MakeArguments(OperationParameters operationParameters)
        {
            Arguments Arguments = new Arguments();
            Arguments.AddAction("BuildCookRun");
            Arguments.AddPath("project", operationParameters.Project.UProjectPath);
            Arguments.AddFlag("build");
            Arguments.AddValue("clientconfig", operationParameters.Configuration.ToString());
            return Arguments;
        }
    }
}
