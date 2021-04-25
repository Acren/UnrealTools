using System;
using System.Diagnostics;

namespace UnrealAutomationCommon.Operations
{
    public delegate void OperationOutput(string output, bool isError);
    public delegate void OperationEnded(OperationResult result);

    public class OperationRunner
    {
        private Operation _operation;
        private OperationParameters _operationParameters;
        private Process _process;

        public event OperationOutput Output;
        public event OperationEnded Ended;

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
            _process = _operation.Execute(_operationParameters, (o, args) =>
            {
                Output?.Invoke(args.Data, false);
            }, (o, args) =>
            {
                Output?.Invoke(args.Data, true);
            }, (o, args) =>
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
        }
    }
}
