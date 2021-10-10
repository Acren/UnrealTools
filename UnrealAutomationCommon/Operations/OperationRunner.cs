using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Operations
{
    public delegate void OperationOutputEventHandler(string output, LogVerbosity verbosity);

    public class OperationRunner : IOperationLogger
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly OperationParameters _operationParameters;
        private int _lineCount;

        public OperationRunner(Operation operation, OperationParameters operationParameters)
        {
            Operation = operation;
            _operationParameters = operationParameters;
        }

        public Operation Operation { get; }

        public event OperationOutputEventHandler Output;

        public void Log(string line, LogVerbosity verbosity)
        {
            OutputLine(line, verbosity);
        }

        public async Task<OperationResult> Run()
        {
            string outputPath = Operation.GetOutputPath(_operationParameters);
            FileUtils.DeleteDirectoryIfExists(outputPath);

            var task = Operation.Execute(_operationParameters, this, _cancellationTokenSource.Token);

            FlagOptions flagOptions = _operationParameters.FindOptions<FlagOptions>();
            if (flagOptions is { WaitForAttach: true }) Output?.Invoke("-WaitForAttach was specified, attach now", LogVerbosity.Log);

            return await task;
        }

        public void Cancel()
        {
            Output?.Invoke("Cancelling operation '" + Operation.OperationName + "'", LogVerbosity.Warning);
            _cancellationTokenSource.Cancel();
        }

        private void OutputLine(string line, LogVerbosity verbosity)
        {
            if (string.IsNullOrEmpty(line)) return;

            _lineCount++;
            Output?.Invoke("[" + _lineCount + "]: " + line, verbosity);
        }
    }
}