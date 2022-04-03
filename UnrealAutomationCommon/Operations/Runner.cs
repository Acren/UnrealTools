using System;
using System.Threading;
using System.Threading.Tasks;
using UnrealAutomationCommon.Operations.BaseOperations;
using UnrealAutomationCommon.Operations.OperationOptionTypes;

namespace UnrealAutomationCommon.Operations
{
    public class Runner : ILogger
    {
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly OperationParameters _operationParameters;
        private int _lineCount;

        //public event Action Ended;
        private Task<OperationResult> _currentTask = null;

        public Runner(Operation operation, OperationParameters operationParameters)
        {
            Operation = operation;
            _operationParameters = operationParameters;
        }

        public bool IsRunning => _currentTask is { IsCompleted: false };

        public Operation Operation { get; }

        public event LogEventHandler Output;

        public void Log(string line, LogVerbosity verbosity)
        {
            OutputLine(line, verbosity);
        }

        public async Task<OperationResult> Run()
        {
            if (_currentTask != null)
            {
                throw new Exception("Task is already running");
            }

            string outputPath = Operation.GetOutputPath(_operationParameters);
            FileUtils.DeleteDirectoryIfExists(outputPath);

            _currentTask = Operation.Execute(_operationParameters, this, _cancellationTokenSource.Token);

            FlagOptions flagOptions = _operationParameters.FindOptions<FlagOptions>();
            if (flagOptions is { WaitForAttach: true })
            {
                Output?.Invoke("-WaitForAttach was specified, attach now", LogVerbosity.Log);
            }

            OperationResult result = await _currentTask;
            Output?.Invoke($"'{Operation.OperationName}' task ended", LogVerbosity.Log);
            _currentTask = null;
            return result;
        }

        public async Task Cancel()
        {
            if (_currentTask == null)
            {
                throw new Exception("Task is not running");
            }

            Output?.Invoke($"Cancelling operation '{Operation.OperationName}'", LogVerbosity.Warning);

            //TaskCompletionSource<bool> tcs = new();
            //Ended += () => tcs.TrySetResult(true);

            _cancellationTokenSource.Cancel();

            await _currentTask;

            Output?.Invoke($"'{Operation.OperationName}' task ended from cancellation", LogVerbosity.Warning);
        }

        private void OutputLine(string line, LogVerbosity verbosity)
        {
            if (line == null)
            {
                throw new Exception("Null line");
            }

            _lineCount++;
            Output?.Invoke("[" + _lineCount + "]: " + line, verbosity);
        }
    }
}