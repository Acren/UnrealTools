using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Operations
{
    public delegate void OperationOutputEventHandler(string output, LogVerbosity verbosity);
    public delegate void OperationEndedEventHandler(OperationResult result);

    public class OperationRunner : IOperationLogger
    {
        private Operation _operation;
        private OperationParameters _operationParameters;
        private int _lineCount = 0;

        public event OperationOutputEventHandler Output;
        public event OperationEndedEventHandler Ended;

        public OperationRunner(Operation operation, OperationParameters operationParameters)
        {
            _operation = operation;
            _operationParameters = operationParameters;
        }

        public void Run()
        {
            string outputPath = _operation.GetOutputPath(_operationParameters);
            FileUtils.DeleteDirectoryIfExists(outputPath);

            _operation.Ended += result =>
            {
                Ended?.Invoke(result);
            };
            _operation.Execute(_operationParameters, this);

            FlagOptions flagOptions = _operationParameters.FindOptions<FlagOptions>();
            if(flagOptions.WaitForAttach)
            {
                Output?.Invoke("-WaitForAttach was specified, attach now", LogVerbosity.Log);
            }
        }

        public void Terminate()
        {
            Output?.Invoke("Terminating operation '" + _operation.OperationName + "'", LogVerbosity.Warning);
            _operation.Terminate();
        }

        void OutputLine(string line, LogVerbosity verbosity)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            _lineCount++;
            Output?.Invoke("[" + _lineCount + "]: " + line, verbosity);
        }

        public void Log(string line, LogVerbosity verbosity)
        {
            OutputLine(line, verbosity);
        }
    }
}
