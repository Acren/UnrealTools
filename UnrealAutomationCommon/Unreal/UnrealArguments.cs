using System.Collections.Generic;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Unreal
{
    public class UnrealArguments
    {
        public static Arguments MakeArguments(OperationParameters operationParameters, string outputhPath, bool uProjectPath = false)
        {
            Arguments arguments = new();

            if (uProjectPath && operationParameters.Target is Project project) arguments.SetPath(project.UProjectPath);

            arguments.SetFlag("stdout");
            arguments.SetFlag("FullStdOutLogOutput");
            arguments.SetFlag("nologtimes");

            bool useInsights = operationParameters.RequestOptions<InsightsOptions>().TraceChannels.Count > 0;

            if (useInsights)
            {
                var traceChannels = new List<string>();
                foreach (TraceChannel channel in operationParameters.RequestOptions<InsightsOptions>().TraceChannels) traceChannels.Add(channel.Key);

                arguments.SetKeyValue("trace", string.Join(",", traceChannels));

                if (traceChannels.Contains("cpu")) arguments.SetFlag("statnamedevents");

                arguments.SetKeyValue("tracehost", "127.0.0.1");
            }

            if (operationParameters.RequestOptions<FlagOptions>().StompMalloc) arguments.SetFlag("stompmalloc");

            if (operationParameters.RequestOptions<FlagOptions>().WaitForAttach) arguments.SetFlag("waitforattach");

            AutomationOptions automationOpts = operationParameters.RequestOptions<AutomationOptions>();
            if (automationOpts.RunTests)
            {
                string testNameOverride = automationOpts.TestNameOverride;
                string testName = !string.IsNullOrEmpty(testNameOverride) ? testNameOverride : operationParameters.Target.TestName;
                string execCmds = $"Automation RunTests {testName};Quit";
                arguments.SetKeyValue("ExecCmds", execCmds);
                arguments.SetKeyPath("ReportExportPath", OutputPaths.GetTestReportPath(outputhPath));
                if (automationOpts.Headless)
                {
                    // Run tests as unattended and headless
                    arguments.SetFlag("unattended");
                    arguments.SetFlag("nullrhi");
                    // Disable tutorial to prevent crash from nullrhi
                    arguments.SetFlag("ini:EditorSettings:[/Script/IntroTutorials.EditorTutorialSettings]:StartupTutorial=");
                }
                else
                {
                    arguments.SetFlag("windowed");
                    arguments.SetKeyValue("resx", "640");
                    arguments.SetKeyValue("resy", "360");
                }
            }

            arguments.AddAdditionalArguments(operationParameters);

            return arguments;
        }
    }
}