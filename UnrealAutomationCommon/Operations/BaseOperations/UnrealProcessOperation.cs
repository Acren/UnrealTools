using System;
using System.Linq;
using UnrealAutomationCommon.Operations.OperationOptionTypes;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations.BaseOperations
{
    // Operation type for running Unreal processes
    public abstract class UnrealProcessOperation<T> : CommandProcessOperation<T> where T : OperationTarget
    {
        protected override void OnProcessEnded(OperationResult result)
        {
            // Report test results
            AutomationOptions automationOptions = OperationParameters.FindOptions<AutomationOptions>();
            if (!Cancelled && automationOptions is { RunTests: { Value: true } })
            {
                IEngineInstallProvider engineInstallProvider = OperationParameters.Target as IEngineInstallProvider;
                if (engineInstallProvider == null)
                {
                    throw new Exception("Target does not provide engine install");
                }
                bool engineSupportsReports = engineInstallProvider.EngineInstall.SupportsTestReports;
                if (!engineSupportsReports)
                {
                    Logger.Log("Engine version does not support test reports, so results cannot be checked", LogVerbosity.Warning);
                }
                else
                {
                    string reportFilePath = OutputPaths.GetTestReportFilePath(GetOutputPath(OperationParameters));
                    TestReport report = TestReport.Load(reportFilePath);
                    if (report != null)
                        result.TestReport = report;
                    else
                        throw new Exception("Expected test report at " + reportFilePath + " but didn't find one");

                    if (result.TestReport != null)
                    {
                        foreach (Test test in result.TestReport.Tests)
                        {
                            Logger.Log(EnumUtils.GetName(test.State).ToUpperInvariant().PadRight(7) + " - " + test.FullTestPath, test.State == TestState.Success ? LogVerbosity.Log : LogVerbosity.Error);
                            foreach (TestEntry entry in test.Entries)
                                if (entry.Event.Type != TestEventType.Info)
                                    Logger.Log("".PadRight(9) + " - " + entry.Event.Message, entry.Event.Type == TestEventType.Error ? LogVerbosity.Error : LogVerbosity.Warning);
                        }

                        int testsPassed = result.TestReport.Tests.Count(t => t.State == TestState.Success);
                        bool allPassed = testsPassed == result.TestReport.Tests.Count;
                        Logger.Log(testsPassed + " of " + result.TestReport.Tests.Count + " tests passed", allPassed ? LogVerbosity.Log : LogVerbosity.Error);
                    }

                    if (report.Failed > 0) throw new Exception("Tests failed");
                }

            }
        }
    }
}