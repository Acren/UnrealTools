using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public delegate void OperationOutputEventHandler(string output, UnrealLogVerbosity verbosity);
    public delegate void OperationEndedEventHandler(OperationResult result);

    public class OperationRunner
    {
        private Operation _operation;
        private OperationParameters _operationParameters;
        private Process _process;
        private bool _isWaitingForLogs = false;

        public event OperationOutputEventHandler Output;
        public event OperationEndedEventHandler Ended;

        public static OperationRunner Run(Operation operation, OperationParameters operationParameters)
        {
            OperationRunner runner = new OperationRunner()
            {
                _operation = operation,
                _operationParameters = operationParameters
            };
            runner.Run();
            return runner;
        }

        void Run()
        {
            bool readLogFile = _operation.ShouldReadOutputFromLogFile() && _operationParameters.Target is Project;

            if (readLogFile)
            {
                Project project = _operationParameters.Target as Project;
                LogWatcher logWatcher = new LogWatcher(project);
                logWatcher.LineLogged += HandleLogLine;
            }

            _process = _operation.Execute(_operationParameters, (o, args) =>
            {
                HandleLogLine(args.Data);
            }, (o, args) =>
            {
                Output?.Invoke(args.Data, UnrealLogVerbosity.Error);
            }, (o, args) =>
            {
                OnProcessEnded();
            });
        }

        void OnProcessEnded()
        {
            if (_isWaitingForLogs)
            {
                return;
            }

            // Wait a little for logs to finish reading

            _isWaitingForLogs = true;

            Task.Delay(100).ContinueWith(t =>
            {
                HandleProcessEnded();
            });
        }

        void HandleLogLine(string line)
        {
            if (line == null)
            {
                return;
            }

            string[] split = line.Split(new []{": "}, StringSplitOptions.None);
            UnrealLogVerbosity verbosity = UnrealLogVerbosity.Log;
            if (split.Length > 1)
            {
                if (split[1] == "Error")
                {
                    verbosity = UnrealLogVerbosity.Error;
                }
                else if (split[1] == "Warning")
                {
                    verbosity = UnrealLogVerbosity.Warning;
                }
            }
            Output?.Invoke(line, verbosity);
        }

        void HandleProcessEnded()
        {
            OperationResult result = new OperationResult();
            result.ExitCode = _process.ExitCode;

            Output?.Invoke("Process exited with code " + result.ExitCode, result.ExitCode == 0 ? UnrealLogVerbosity.Log : UnrealLogVerbosity.Error);

            if (_operationParameters.RunTests)
            {
                string reportFilePath = OutputPaths.GetTestReportFilePath(_operation.GetOutputPath(_operationParameters));
                TestReport report = TestReport.Load(reportFilePath);
                if (report != null)
                {
                    result.TestReport = report;
                }
                else
                {
                    Output?.Invoke("Expected test report at " + reportFilePath + " but didn't find one", UnrealLogVerbosity.Error);
                }

                if (result.TestReport != null)
                {
                    foreach (Test test in result.TestReport.Tests)
                    {
                        Output?.Invoke(EnumUtils.GetName(test.State).ToUpperInvariant().PadRight(7) + " - " + test.FullTestPath, test.State == TestState.Success ? UnrealLogVerbosity.Log : UnrealLogVerbosity.Error);
                    }
                }
            }

            Ended?.Invoke(result);
        }
    }
}
