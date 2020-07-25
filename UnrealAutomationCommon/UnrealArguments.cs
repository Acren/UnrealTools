using System.ComponentModel;
using System.Runtime.CompilerServices;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public class UnrealArguments
    {
        public static Arguments MakeArguments(OperationParameters operationParameters)
        {
            Arguments Arguments = new Arguments();

            if(operationParameters.UseInsights)
            {
                Arguments.AddValue("trace", "cpu,frame,bookmark");
                Arguments.AddFlag("statnamedevents");
                Arguments.AddValue("tracehost", "127.0.0.1");
            }

            if (operationParameters.StompMalloc)
            {
                Arguments.AddFlag("stompmalloc");
            }

            return Arguments;
        }

    }
}
