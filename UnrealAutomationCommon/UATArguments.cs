using System;
using System.Collections.Generic;
using System.Text;

namespace UnrealAutomationCommon
{
    public class UATArguments
    {
        public static string ToString(OperationParameters operationParameters)
        {
            Arguments arguments = new Arguments();
            arguments.AddAction("BuildCookRun");
            arguments.AddPath("project", operationParameters.Project.UProjectPath);
            arguments.AddFlag("build");
            arguments.AddValue("clientconfig", operationParameters.Configuration.ToString());
            return arguments.ToString();
        }
    }
}
