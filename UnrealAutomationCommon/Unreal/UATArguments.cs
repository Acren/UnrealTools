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
            Arguments arguments = new Arguments();
            arguments.SetArgument("BuildCookRun");
            if (operationParameters.Target is Project project)
            {
                arguments.SetKeyPath("project", project.UProjectPath);
            }
            arguments.SetFlag("build");
            string configuration = operationParameters.Configuration.ToString();
            arguments.SetKeyValue("clientconfig", configuration);
            arguments.SetKeyValue("serverconfig", configuration);

            if (!string.IsNullOrWhiteSpace(operationParameters.AdditionalArguments))
            {
                arguments.AddRawArgsString(operationParameters.AdditionalArguments);
            }

            return arguments;
        }
    }
}
