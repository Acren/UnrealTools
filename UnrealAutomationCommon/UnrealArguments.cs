using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UnrealAutomationCommon
{
    public class UnrealArguments
    {
        public static string ToString(OperationParameters operationParameters)
        {
            Arguments arguments = new Arguments();
            if(operationParameters.UseInsights)
            {
                arguments.AddValue("trace", "cpu,frame,bookmark");
                arguments.AddFlag("statnamedevents");
                arguments.AddValue("tracehost", "127.0.0.1");
            }
            return arguments.ToString();
        }

    }
}
