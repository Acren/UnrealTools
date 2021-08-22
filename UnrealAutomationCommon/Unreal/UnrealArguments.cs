﻿using System.Collections.Generic;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
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

            arguments.SetFlag("stdout");
            arguments.SetFlag("FullStdOutLogOutput");
            arguments.SetFlag("nologtimes");

            bool useInsights = operationParameters.RequestOptions<InsightsOptions>().TraceChannels.Count > 0;
 
            if (useInsights)
            {
                List<string> traceChannels = new List<string>();
                foreach (TraceChannel channel in operationParameters.RequestOptions<InsightsOptions>().TraceChannels)
                {
                    traceChannels.Add(channel.Key);
                }

                arguments.SetKeyValue("trace", string.Join(",",traceChannels));

                if (traceChannels.Contains("cpu"))
                {
                    arguments.SetFlag("statnamedevents");
                }

                arguments.SetKeyValue("tracehost", "127.0.0.1");
            }

            if (operationParameters.RequestOptions<FlagOptions>().StompMalloc)
            {
                arguments.SetFlag("stompmalloc");
            }

            if (operationParameters.RequestOptions<FlagOptions>().WaitForAttach)
            {
                arguments.SetFlag("waitforattach");
            }

            AutomationOptions automationOpts = operationParameters.RequestOptions<AutomationOptions>();
            if (automationOpts.RunTests)
            {
                string testNameOverride = automationOpts.TestNameOverride;
                string testName = !string.IsNullOrEmpty(testNameOverride) ? testNameOverride : operationParameters.Target.TestName;
                string execCmds = "Automation RunTests " + testName;
                arguments.SetKeyValue("ExecCmds", execCmds);
                arguments.SetKeyPath("ReportExportPath", OutputPaths.GetTestReportPath(outputhPath));
                arguments.SetKeyValue("testexit", "Automation Test Queue Empty");
                if (automationOpts.Headless)
                {
                    // Run tests as unattended and headless
                    arguments.SetFlag("unattended");
                    arguments.SetFlag("nullrhi");
                    arguments.SetFlag("server");
                }
                else
                {
                    arguments.SetFlag("windowed");
                    arguments.SetKeyValue("resx", "640");
                    arguments.SetKeyValue("resy", "360");
                }
            }

            if (!string.IsNullOrWhiteSpace(operationParameters.AdditionalArguments))
            {
                arguments.AddRawArgsString(operationParameters.AdditionalArguments);
            }

            return arguments;
        }

    }
}
