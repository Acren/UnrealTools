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
            Arguments.AddArgument("BuildCookRun");
            Arguments.AddKeyPath("project", operationParameters.Project.UProjectPath);
            Arguments.AddFlag("build");
            string configuration = operationParameters.Configuration.ToString();
            Arguments.AddKeyValue("clientconfig", configuration);
            Arguments.AddKeyValue("serverconfig", configuration);
            return Arguments;
        }
    }
}
