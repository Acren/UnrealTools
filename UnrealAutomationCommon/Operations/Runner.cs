using System;
using System.Collections.Generic;
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

        private Task<OperationResult> _currentTask = null;
        private List<string> _errors = new();
        private List<string> _warnings = new();

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

            _currentTask = Operation.ExecuteOnThread(_operationParameters, this, _cancellationTokenSource.Token);

            FlagOptions flagOptions = _operationParameters.FindOptions<FlagOptions>();
            if (flagOptions is { WaitForAttach: true })
            {
                Output?.Invoke("-WaitForAttach was specified, attach now", LogVerbosity.Log);
            }

            OperationResult result = await _currentTask;
            Output?.Invoke($"'{Operation.OperationName}' task ended", LogVerbosity.Log);
            _currentTask = null;

            // Log error and warning summary

            if (_warnings.Count > 0)
            {
                Output?.Invoke("Warnings:", LogVerbosity.Warning);
                foreach (string warning in _warnings)
                {
                    Output?.Invoke(warning, LogVerbosity.Warning);
                }
            }

            if (_errors.Count > 0)
            {
                Output?.Invoke("Errors:", LogVerbosity.Error);
                foreach (string error in _errors)
                {
                    Output?.Invoke(error, LogVerbosity.Error);
                }
            }

            Output?.Invoke($"'{Operation.OperationName}' finished running", LogVerbosity.Log);

            return result;
        }

        public async Task Cancel()
        {
            if (_currentTask == null)
            {
                throw new Exception("Task is not running");
            }

            Output?.Invoke($"Cancelling operation '{Operation.OperationName}'", LogVerbosity.Warning);

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

            switch (verbosity)
            {
                case LogVerbosity.Error:
                {
                    _errors.Add(line);
                    break;
                }
                case LogVerbosity.Warning:
                {
                    _warnings.Add(line);
                    break;
                }
            }
        }
    }
}