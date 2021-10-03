using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Operations
{
    public delegate void OperationOutputEventHandler(string output, LogVerbosity verbosity);

    public class OperationRunner : IOperationLogger
    {
        public Operation Operation { get; private set; }
        private OperationParameters _operationParameters;
        private int _lineCount = 0;

        private CancellationTokenSource _cancellationTokenSource = new();

        public event OperationOutputEventHandler Output;

        public OperationRunner(Operation operation, OperationParameters operationParameters)
        {
            Operation = operation;
            _operationParameters = operationParameters;
        }

        public async Task<OperationResult> Run()
        {
            string outputPath = Operation.GetOutputPath(_operationParameters);
            FileUtils.DeleteDirectoryIfExists(outputPath);

            Task<OperationResult> task = Operation.Execute(_operationParameters, this, _cancellationTokenSource.Token);

            FlagOptions flagOptions = _operationParameters.FindOptions<FlagOptions>();
            if (flagOptions.WaitForAttach)
            {
                Output?.Invoke("-WaitForAttach was specified, attach now", LogVerbosity.Log);
            }

            return await task;
        }

        public void Cancel()
        {
            Output?.Invoke("Cancelling operation '" + Operation.OperationName + "'", LogVerbosity.Warning);
            _cancellationTokenSource.Cancel();
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
