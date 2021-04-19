using System.Collections.Generic;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public class UnrealArguments
    {
        public static Arguments MakeArguments(OperationParameters operationParameters, bool uProjectPath = false)
        {
            Arguments Arguments = new Arguments();

            if (uProjectPath && operationParameters.Target is Project project)
            {
                Arguments.AddPath(project.UProjectPath);
            }

            bool UseInsights = operationParameters.TraceCpu
                               || operationParameters.TraceFrame
                               || operationParameters.TraceBookmark
                               || operationParameters.TraceLoadTime;
            if (UseInsights)
            {
                List<string> TraceChannels = new List<string>();

                if (operationParameters.TraceCpu) TraceChannels.Add("cpu");
                if (operationParameters.TraceFrame) TraceChannels.Add("frame");
                if (operationParameters.TraceBookmark) TraceChannels.Add("bookmark");
                if (operationParameters.TraceLoadTime) TraceChannels.Add("loadtime");

                Arguments.AddKeyValue("trace", string.Join(",",TraceChannels));

                if (operationParameters.TraceCpu)
                {
                    Arguments.AddFlag("statnamedevents");
                }

                Arguments.AddKeyValue("tracehost", "127.0.0.1");
            }

            if (operationParameters.StompMalloc)
            {
                Arguments.AddFlag("stompmalloc");
            }

            if (operationParameters.WaitForAttach)
            {
                Arguments.AddFlag("waitforattach");
            }

            return Arguments;
        }

    }
}
