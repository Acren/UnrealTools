using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnrealAutomationCommon.Unreal;

namespace UnrealAutomationCommon.Operations
{
    public delegate void OperationOutputEventHandler(string output, bool isError);
    public delegate void OperationEndedEventHandler(OperationResult result);

    public class OperationRunner
    {
        private Operation _operation;
        private OperationParameters _operationParameters;
        private Process _process;

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

            if(readLogFile)
            {
                Project project = _operationParameters.Target as Project;
                LogWatcher logWatcher = new LogWatcher(project);
                logWatcher.LineLogged += S =>
                {
                    Output?.Invoke(S, false);
                };
            }

            _process = _operation.Execute(_operationParameters, (o, args) =>
            {
                Output?.Invoke(args.Data, false);
            }, (o, args) =>
            {
                Output?.Invoke(args.Data, true);
            }, (o, args) =>
            {
                // Wait a little for logs to finish reading
                Task.Delay(100).ContinueWith(t =>
                {
                    OperationResult result = new OperationResult();
                    result.ExitCode = _process.ExitCode;

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
                            Output?.Invoke("Expected test report at " + reportFilePath + " but didn't find one", true);
                        }
                    }

                    Ended?.Invoke(result);
                });

            });
        }
    }
}
