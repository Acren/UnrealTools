using System;
using System.Linq;
using UnrealAutomationCommon.Operations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;
using Microsoft.Extensions.Logging;

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    // Operation type for running Unreal processes
    public abstract class UnrealProcessOperation<T> : CommandProcessOperation<T> where T : global::LocalAutomation.Runtime.OperationTarget
    {
        protected override void OnProcessEnded(OperationResult result)
        {
            // Report test results
            AutomationOptions automationOptions = OperationParameters.FindOptions<AutomationOptions>();
            if (!Cancelled && automationOptions is { RunTests: { Value: true } })
            {
                IEngineInstanceProvider engineInstanceProvider = OperationParameters.Target as IEngineInstanceProvider;
                if (engineInstanceProvider == null)
                {
                    throw new Exception("Target does not provide engine install");
                }
                bool engineSupportsReports = engineInstanceProvider.EngineInstance.SupportsTestReports;
                if (!engineSupportsReports)
                {
                    Logger.LogWarning("Engine version does not support test reports, so results cannot be checked");
                }
                else
                {
                    string reportFilePath = OutputPaths.GetTestReportFilePath(GetOutputPath(OperationParameters));
                    TestReport report = TestReport.Load(reportFilePath);
                    if (report == null)
                    {
                        throw new Exception("Expected test report at " + reportFilePath + " but didn't find one");
                    }

                    if (report != null)
                    {
                        foreach (Test test in report.Tests)
                        {
                            Logger.Log(test.State == TestState.Success ? LogLevel.Information : LogLevel.Error, EnumUtils.GetName(test.State).ToUpperInvariant().PadRight(7) + " - " + test.FullTestPath);
                            foreach (TestEntry entry in test.Entries)
                                if (entry.Event.Type != TestEventType.Info)
                                {
                                    Logger.Log(entry.Event.Type == TestEventType.Error ? LogLevel.Error : LogLevel.Warning, "".PadRight(9) + " - " + entry.Event.Message);
                                }
                        }

                        int testsPassed = report.Tests.Count(t => t.State == TestState.Success);
                        bool allPassed = testsPassed == report.Tests.Count;
                        Logger.Log(allPassed ? LogLevel.Information : LogLevel.Error, testsPassed + " of " + report.Tests.Count + " tests passed");
                    }

                    if (report.Failed > 0)
                    {
                        throw new Exception("Tests failed");
                    }
                }

            }
        }
    }
}
