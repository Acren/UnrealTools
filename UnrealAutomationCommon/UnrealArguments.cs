using System.Collections.Generic;
using System.IO;
using UnrealAutomationCommon.Operations;

namespace UnrealAutomationCommon
{
    public class UnrealArguments
    {
        public static Arguments MakeArguments(OperationParameters operationParameters, string outputhPath, bool uProjectPath = false)
        {
            Arguments arguments = new Arguments();

            if (uProjectPath && operationParameters.Target is Project project)
            {
                arguments.SetPath(project.UProjectPath);
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

                arguments.SetKeyValue("trace", string.Join(",",TraceChannels));

                if (operationParameters.TraceCpu)
                {
                    arguments.SetFlag("statnamedevents");
                }

                arguments.SetKeyValue("tracehost", "127.0.0.1");
            }

            if (operationParameters.StompMalloc)
            {
                arguments.SetFlag("stompmalloc");
            }

            if (operationParameters.WaitForAttach)
            {
                arguments.SetFlag("waitforattach");
            }

            if (operationParameters.RunTests && operationParameters.Target is Project Project)
            {
                string execCmds = "Automation RunTests " + Project?.TestName;
                arguments.SetKeyValue("ExecCmds", execCmds);
                arguments.SetKeyPath("ReportOutputPath", OutputPaths.GetTestReportPath(outputhPath));
                arguments.SetKeyValue("testexit", "Automation Test Queue Empty");
                arguments.SetFlag("windowed");
                arguments.SetKeyValue("resx", "640");
                arguments.SetKeyValue("resy", "360");
            }

            return arguments;
        }

    }
}
