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
        /// <summary>
        /// Unreal process launches always expose tracing, flag, and automation option groups because shared argument
        /// construction reads all three when building the command line.
        /// </summary>
        protected override void CollectRequiredOptionSetTypes(global::LocalAutomation.Runtime.IOperationTarget target, System.Collections.Generic.ISet<System.Type> optionSetTypes)
        {
            base.CollectRequiredOptionSetTypes(target, optionSetTypes);
            optionSetTypes.Add(typeof(InsightsOptions));
            optionSetTypes.Add(typeof(FlagOptions));
            optionSetTypes.Add(typeof(AutomationOptions));
        }

        protected override void OnProcessEnded(global::LocalAutomation.Runtime.OperationResult result)
        {
            // Report test results
            AutomationOptions automationOptions = UnrealOperationParameters.GetOptions<AutomationOptions>();
            if (!Cancelled && automationOptions.RunTests)
            {
                if (UnrealOperationParameters.Target is not IEngineInstanceProvider engineInstanceProvider)
                {
                    throw new Exception("Target does not provide engine install");
                }
                Engine? engine = engineInstanceProvider.EngineInstance;
                if (engine == null)
                {
                    throw new Exception("Target could not resolve an engine install");
                }

                bool engineSupportsReports = engine.SupportsTestReports;
                if (!engineSupportsReports)
                {
                    Logger.LogWarning("Engine version does not support test reports, so results cannot be checked");
                }
                else
                {
                    string reportFilePath = OutputPaths.GetTestReportFilePath(GetOutputPath(UnrealOperationParameters));
                    TestReport? report = TestReport.Load(reportFilePath);
                    if (report == null)
                    {
                        throw new Exception("Expected test report at " + reportFilePath + " but didn't find one");
                    }

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

                    if (report.Failed > 0)
                    {
                        throw new Exception("Tests failed");
                    }
                }

            }
        }
    }
}
