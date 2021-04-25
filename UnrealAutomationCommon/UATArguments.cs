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
            Arguments.SetArgument("BuildCookRun");
            if (operationParameters.Target is Project project)
            {
                Arguments.SetKeyPath("project", project.UProjectPath);
            }
            Arguments.SetFlag("build");
            string configuration = operationParameters.Configuration.ToString();
            Arguments.SetKeyValue("clientconfig", configuration);
            Arguments.SetKeyValue("serverconfig", configuration);
            return Arguments;
        }
    }
}
